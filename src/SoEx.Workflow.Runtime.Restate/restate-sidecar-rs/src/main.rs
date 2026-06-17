//! SoEx.Workflow — Restate sidecar (Rust).
//!
//! Restate ships no .NET SDK, so on this runtime the flow runs out-of-process in the sidecar's own
//! language, calling back to .NET over HTTP. This one binary serves BOTH consumption models — the
//! consumer picks one per instance (never migrate between them) — as two distinct Restate services
//! on one endpoint:
//!
//!   - Portable flow: `OnboardWorkflow` — a GENERIC driver whose `run` handler owns the durable step
//!     loop and maps the .NET component's returned `WorkflowAction` (flattened to `ActionDto`) onto
//!     Restate's durable primitives. Calls `/step` + `/terminate`.
//!   - Native flow: `NativeOnboardWorkflow` — the consumer authors the flow HERE; .NET only governs
//!     each step + the termination. Calls `/gov-step` + `/gov-terminate`.
//!
//! Payloads are opaque base64 strings end to end. The .NET step host URL is STEP_URL (default
//! http://localhost:9090).

use restate_sdk::prelude::*;
use serde::{Deserialize, Serialize};
use std::collections::HashSet;
use std::sync::{Mutex, OnceLock};
use std::time::Duration;

/// Hard cap on distinct interned event names. Each distinct name is leaked once (`Box::leak`) to satisfy
/// `ctx.promise(key)`'s `'static` lifetime; business event names are a small finite set, so this cap sits
/// far above any real flow and only fires on misuse/abuse that would otherwise leak memory unboundedly.
const MAX_INTERNED_EVENT_NAMES: usize = 4096;

/// Interns an event name to `'static`, leaking each DISTINCT name at most once, up to a hard cap.
/// `ctx.promise(key)` binds the key to the context lifetime and so needs `'static`; without interning,
/// every wait execution would leak afresh (unbounded). Past the cap this fails the invocation with a
/// terminal error rather than leaking without bound.
fn intern_event_name(name: String) -> Result<&'static str, HandlerError> {
    static INTERNED: OnceLock<Mutex<HashSet<&'static str>>> = OnceLock::new();
    let set = INTERNED.get_or_init(|| Mutex::new(HashSet::new()));
    let mut guard = set.lock().expect("interned event-name set poisoned");
    intern_into(&mut guard, name, MAX_INTERNED_EVENT_NAMES)
}

/// The testable core of [`intern_event_name`] (no process-global state): returns the existing `'static`
/// pointer for `name`, else leaks-and-inserts it — unless the set is already at `cap`, in which case it
/// returns a terminal error instead of leaking without bound.
fn intern_into(set: &mut HashSet<&'static str>, name: String, cap: usize) -> Result<&'static str, HandlerError> {
    if let Some(existing) = set.get(name.as_str()) {
        return Ok(existing);
    }
    if set.len() >= cap {
        return Err(TerminalError::new(format!(
            "event-name interning cap ({cap}) exceeded — too many distinct event names for this sidecar"
        ))
        .into());
    }
    let leaked: &'static str = Box::leak(name.into_boxed_str());
    set.insert(leaked);
    Ok(leaked)
}

/// Convert .NET ticks (100ns units) to a `Duration`. Clamps negatives to zero and saturates the `* 100`,
/// so a hostile or buggy huge ticks value yields a long-but-finite duration instead of overflowing `u64`
/// (which would panic in a debug build and wrap to ~0 in release).
fn ticks_to_duration(ticks: i64) -> Duration {
    Duration::from_nanos((ticks.max(0) as u64).saturating_mul(100))
}

/// Split a Restate execution key into the logical instance id and the resume sequence. Continue-as-new
/// appends a `~<seq>` generation suffix for a fresh journal; the logical instance id (for keying and
/// governance) is the base before the first `~`, and the sequence resumes from the suffix so the
/// idempotency key (InstanceId, DtoType, Sequence) stays unique across generations. A first run (no
/// suffix, or an unparseable one) starts at sequence 0.
fn parse_exec_key(exec_key: &str) -> (String, i64) {
    let instance_id = exec_key.split('~').next().unwrap_or(exec_key).to_string();
    let sequence = exec_key.split('~').nth(1).and_then(|s| s.parse().ok()).unwrap_or(0);
    (instance_id, sequence)
}

/// Pick the envelope to resume a wait into, or fail descriptively when there is none: `primary` (the raised
/// payload, or the on-timeout step) wins if non-empty, else `fallback` (the on-event step) if non-empty, else
/// a terminal error — so an empty resume does not continue with an empty envelope that would only fail later
/// at decrypt. Matches the InProc driver's descriptive throw instead of diverging from it.
fn resume_or_fail(primary: String, fallback: String, event_name: &str, what: &str) -> Result<String, HandlerError> {
    if !primary.is_empty() {
        Ok(primary)
    } else if !fallback.is_empty() {
        Ok(fallback)
    } else {
        Err(TerminalError::new(format!("'{event_name}' {what}")).into())
    }
}

/// Payload of either workflow's `raise_event` handler:
/// `{ "name": <promise>, "payload": <base64>, "raiseId": <optional id> }`.
///
/// `raise_id` is advisory on Restate: raises here are deduped by the durable promise itself, which is
/// write-once per event NAME (see the handler), so a re-raise is already a no-op regardless of id — the
/// id is carried only so the cross-engine gateway can pass it uniformly. The gateway sends it camelCase.
#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct RaiseEvent {
    name: String,
    payload: String,
    #[serde(default)]
    #[allow(dead_code)]
    raise_id: Option<String>,
}

// ===========================================================================================
// Portable flow — generic driver. Owns the step loop; maps WorkflowAction onto Restate.
// ===========================================================================================

/// Request to the .NET step host `/step`. camelCase to match the ASP.NET minimal-API binder.
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct StepRequest {
    payload: String,
    instance_id: String,
    sequence: i64,
}

/// Request to the .NET step host `/terminate` (the termination erasure lifecycle).
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct TerminateRequest {
    instance_id: String,
    sequence: i64,
}

/// Flattened `WorkflowAction` returned by `/step`. byte[] fields arrive as base64 strings;
/// unknown JSON fields are dropped by serde.
#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ActionDto {
    kind: String,
    #[serde(default)]
    payload: String,
    #[serde(default)]
    event_name: String,
    #[serde(default)]
    timeout_ticks: i64,
    /// The step to resume into when a `wait` timer wins the race (base64 envelope).
    #[serde(default)]
    on_timeout: String,
    /// The step to resume into when a `wait` signal arrives with an empty payload (base64
    /// envelope) — the flow's pre-sealed continuation; a non-empty raised payload wins.
    #[serde(default)]
    on_event: String,
}

/// POST a governed step to the .NET step host and return the flattened action. The sealed `payload` is
/// forwarded verbatim — the sidecar never inspects or mutates it — with the shared bearer token attached;
/// a non-2xx response surfaces as an error (caught by the durable `ctx.run`). Extracted so the transport/auth
/// contract is integration-testable against a mock host without the restate runtime.
async fn call_step(client: &reqwest::Client, step_url: &str, token: &str, req: &StepRequest) -> reqwest::Result<ActionDto> {
    client
        .post(format!("{step_url}/step"))
        .bearer_auth(token)
        .json(req)
        .send()
        .await?
        .error_for_status()?
        .json::<ActionDto>()
        .await
}

/// POST the termination (crypto-shred) to the .NET step host with the shared bearer token; a non-2xx
/// response surfaces as an error.
async fn call_terminate(client: &reqwest::Client, step_url: &str, token: &str, req: &TerminateRequest) -> reqwest::Result<()> {
    client
        .post(format!("{step_url}/terminate"))
        .bearer_auth(token)
        .json(req)
        .send()
        .await?
        .error_for_status()?;
    Ok(())
}

#[restate_sdk::workflow]
#[name = "OnboardWorkflow"]
trait OnboardWorkflow {
    #[name = "run"]
    async fn run(seed: String) -> Result<String, HandlerError>;

    #[name = "raise_event"]
    #[shared]
    async fn raise_event(req: Json<RaiseEvent>) -> Result<(), HandlerError>;
}

struct OnboardWorkflowImpl {
    client: reqwest::Client,
    step_url: String,
    token: String,
}

impl OnboardWorkflow for OnboardWorkflowImpl {
    async fn run(&self, ctx: WorkflowContext<'_>, seed: String) -> Result<String, HandlerError> {
        // The Restate workflow id is unique per execution; continue-as-new appends a `~<seq>`
        // generation suffix for a fresh journal. The logical instance id is the base before the first `~`,
        // so the per-instance key is carried across continue-as-new and shredded only at the real
        // termination; the sequence resumes from the suffix. See parse_exec_key.
        let (instance_id, mut sequence) = parse_exec_key(&ctx.key().to_string());
        let mut current = seed;

        loop {
            // one Manager step, journalled durably
            let req = StepRequest {
                payload: current.clone(),
                instance_id: instance_id.clone(),
                sequence,
            };
            let (client, url, token) = (self.client.clone(), self.step_url.clone(), self.token.clone());
            let dto: ActionDto = ctx
                .run(|| async move { Ok(Json(call_step(&client, &url, &token, &req).await?)) })
                .name(format!("step-{sequence}"))
                .await?
                .into_inner();
            sequence += 1;

            match dto.kind.as_str() {
                "complete" => {
                    let term = TerminateRequest {
                        instance_id: instance_id.clone(),
                        sequence,
                    };
                    let (client, url, token) = (self.client.clone(), self.step_url.clone(), self.token.clone());
                    ctx.run(|| async move {
                        call_terminate(&client, &url, &token, &term).await?;
                        Ok(())
                    })
                    .name(format!("terminate-{sequence}"))
                    .await?;
                    return Ok(dto.payload);
                }
                "next" => current = dto.payload,
                "delay" => {
                    ctx.sleep(ticks_to_duration(dto.timeout_ticks)).await?;
                }
                "wait" => {
                    // promise() binds its key to the context lifetime; intern the event name to
                    // 'static (leaking each distinct name at most once) to satisfy it generically.
                    let event: &'static str = intern_event_name(dto.event_name.clone())?;
                    let event_name = dto.event_name.clone();
                    if dto.timeout_ticks < 0 {
                        let resolved = ctx.promise::<String>(event).await?;
                        // an empty raise resumes into the pre-sealed on-event step; a payload wins. With neither
                        // a payload nor an on-event step there is nothing to resume into — fail descriptively
                        // rather than continue with an empty envelope that only fails later at decrypt (matching
                        // the InProc driver instead of diverging from it).
                        current = resume_or_fail(resolved, dto.on_event, &event_name, "was raised with an empty payload and the wait has no on-event step")?;
                    } else {
                        // race the durable promise against a durable timer; if the timer
                        // wins, resume into the on-timeout step (.NET ticks are 100ns units).
                        let dur = ticks_to_duration(dto.timeout_ticks);
                        current = restate_sdk::select! {
                            resolved = ctx.promise::<String>(event) =>
                                resume_or_fail(resolved?, dto.on_event, &event_name, "was raised with an empty payload and the wait has no on-event step")?,
                            _ = ctx.sleep(dur) =>
                                resume_or_fail(dto.on_timeout, String::new(), &event_name, "timer elapsed with no on-timeout step")?,
                        };
                    }
                }
                "loop" => {
                    // continue-as-new: chain a fresh workflow execution carrying the state,
                    // then complete this one — bounding each execution's journal. The next id is
                    // instance_id~<absolute sequence>: unique per generation (deterministic, replay-safe),
                    // it keeps the logical instance id stable (carrying the key) AND lets the next
                    // generation resume the sequence so idempotency keys never collide across generations.
                    let next_id = format!("{instance_id}~{sequence}");
                    // Record this generation's successor so a raise that lands here after the continue-as-new
                    // is forwarded to the live generation (see raise_event) instead of resolving a promise on
                    // this about-to-complete execution — otherwise a Loop-then-WaitForEvent flow deadlocks.
                    ctx.set("next_gen", next_id.clone());
                    ctx.workflow_client::<OnboardWorkflowClient>(next_id)
                        .run(dto.payload)
                        .send();
                    return Ok(String::new());
                }
                other => {
                    // An unhandled action is a TERMINAL (non-retryable) failure: crypto-shred before
                    // propagating so a terminally-failed portable instance doesn't retain its key — the sidecar
                    // analogue of the C# drivers' catch-and-shred. Restate's other failure modes need no shred
                    // here: a transient/retryable step error is retried by Restate and MUST keep its key (so it
                    // can replay), and an administrative cancel/kill bypasses handler code entirely — the
                    // erasure backstop covers that, exactly as management-terminate does on the other engines.
                    let term = TerminateRequest {
                        instance_id: instance_id.clone(),
                        sequence,
                    };
                    let (client, url, token) = (self.client.clone(), self.step_url.clone(), self.token.clone());
                    ctx.run(|| async move {
                        client
                            .post(format!("{url}/terminate"))
                            .bearer_auth(&token)
                            .json(&term)
                            .send()
                            .await?
                            .error_for_status()?;
                        Ok(())
                    })
                    .name(format!("terminate-fail-{sequence}"))
                    .await?;
                    return Err(TerminalError::new(format!("unhandled action kind: {other}")).into());
                }
            }
        }
    }

    async fn raise_event(
        &self,
        ctx: SharedWorkflowContext<'_>,
        req: Json<RaiseEvent>,
    ) -> Result<(), HandlerError> {
        // If this generation has already continued-as-new, the live wait lives in a later generation; resolving
        // the promise here would land on a completed execution and the waiter would never see it. Forward the
        // raise to the recorded successor (each generation knows only its immediate next), walking the chain
        // until it reaches the still-running generation, where resolve_once delivers it idempotently. The
        // gateway always targets the base id, so this internal hop is what makes Loop-then-WaitForEvent work.
        if let Some(next) = ctx.get::<String>("next_gen").await? {
            ctx.workflow_client::<OnboardWorkflowClient>(next)
                .raise_event(req)
                .send();
            return Ok(());
        }

        let RaiseEvent { name, payload, .. } = req.into_inner();
        resolve_once(&ctx, name, payload).await?;
        Ok(())
    }
}

/// Deliver a raise idempotently. A Restate durable promise is write-once per event NAME: the first resolve
/// wins and a later resolve of the same name is dropped by the engine, so a re-raise can never deliver the
/// event twice — dedup is inherent here, not something we add. We peek first and skip the redundant resolve
/// only to make that no-op explicit in our own code (and to avoid the wasted second write). This subsumes the
/// .NET drivers' raise-id dedup: an accidental redelivery — with or without a matching id — can't deliver the
/// event twice. (Restate consequently cannot deliver two distinct raises of one name either; that's the
/// engine's latch model, documented as a divergence — id-based dedup would not change it.)
async fn resolve_once(ctx: &SharedWorkflowContext<'_>, name: String, payload: String) -> Result<(), HandlerError> {
    // promise()/peek_promise() bind the key to the context lifetime; intern to 'static to satisfy it generically.
    let event: &'static str = intern_event_name(name)?;
    if ctx.peek_promise::<String>(event).await?.is_some() {
        return Ok(());   // already delivered — drop the re-raise
    }
    ctx.resolve_promise::<String>(event, payload);
    Ok(())
}

// ===========================================================================================
// Native flow — the consumer authors the flow here; .NET governs each step + termination.
// ===========================================================================================

/// Request to the .NET governed step host `/gov-step`.
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct GovStepRequest {
    instance_id: String,
    seq: i64,
    kind: String,
    data: String,
}

/// Request to the .NET governed step host `/gov-terminate`.
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct GovTerminateRequest {
    instance_id: String,
}

#[restate_sdk::workflow]
#[name = "NativeOnboardWorkflow"]
trait NativeOnboardWorkflow {
    #[name = "run"]
    async fn run(seed: String) -> Result<String, HandlerError>;

    #[name = "raise_event"]
    #[shared]
    async fn raise_event(req: Json<RaiseEvent>) -> Result<(), HandlerError>;
}

struct NativeOnboardWorkflowImpl {
    client: reqwest::Client,
    step_url: String,
    token: String,
}

impl NativeOnboardWorkflowImpl {
    /// Crypto-shred the instance through the .NET termination hook. Idempotent: the .NET side no-ops on an
    /// already-destroyed key, so the success path and the failure-path catch can both call it safely.
    /// `journal_name` keeps the two call sites distinct durable entries (a deterministic replay requirement).
    async fn gov_terminate(
        &self, ctx: &WorkflowContext<'_>, instance_id: &str, journal_name: &str,
    ) -> Result<(), HandlerError> {
        let term = GovTerminateRequest { instance_id: instance_id.to_string() };
        let (client, url, token) = (self.client.clone(), self.step_url.clone(), self.token.clone());
        ctx.run(|| async move {
            client.post(format!("{url}/gov-terminate")).bearer_auth(&token).json(&term).send().await?.error_for_status()?;
            Ok(())
        })
        .name(journal_name.to_string())
        .await?;
        Ok(())
    }
}

impl NativeOnboardWorkflow for NativeOnboardWorkflowImpl {
    async fn run(&self, ctx: WorkflowContext<'_>, seed: String) -> Result<String, HandlerError> {
        // The logical instance id (for keying/governance) is the base before any `~<gen>` suffix,
        // so the per-instance key survives continue-as-new and is shredded only at the real termination.
        let exec_key = ctx.key().to_string();
        let instance_id = exec_key.split('~').next().unwrap_or(&exec_key).to_string();

        // The subject rides inside the sealed `seed` (opaque base64); the flow only threads that
        // ciphertext and names PII-free kinds. The .NET host recovers the subject in memory off the
        // replay path. each step: a governed call to .NET, journalled durably (only the sealed seed).
        //
        // The flow body runs inside a block so a terminal failure can be caught and crypto-shredded before
        // it propagates — the native analogue of the portable flow's catch-and-shred. A transient step error
        // is retried inside ctx.run and never surfaces here, so it keeps its key (it can still replay); only a
        // terminal failure reaches the catch, where the key is no longer needed and must not be retained.
        let flow = async {
            for (seq, kind) in [(0i64, "lookup"), (1, "invite")] {
                let req = GovStepRequest { instance_id: instance_id.clone(), seq, kind: kind.to_string(), data: seed.clone() };
                let (client, url, token) = (self.client.clone(), self.step_url.clone(), self.token.clone());
                ctx.run(|| async move {
                    client.post(format!("{url}/gov-step")).bearer_auth(&token).json(&req).send().await?.error_for_status()?;
                    Ok(())
                })
                .name(format!("gov-step-{seq}"))
                .await?;
            }

            // native durable-promise wait (the flow lives here, in the sidecar)
            let _: String = ctx.promise::<String>("accept").await?;

            let req = GovStepRequest { instance_id: instance_id.clone(), seq: 2, kind: "assign".to_string(), data: seed.clone() };
            let (client, url, token) = (self.client.clone(), self.step_url.clone(), self.token.clone());
            ctx.run(|| async move {
                client.post(format!("{url}/gov-step")).bearer_auth(&token).json(&req).send().await?.error_for_status()?;
                Ok(())
            })
            .name("gov-step-2")
            .await?;

            // termination hook: crypto-shred at the end of the flow
            self.gov_terminate(&ctx, &instance_id, "gov-terminate").await?;

            Ok::<String, HandlerError>("assigned".to_string())
        };

        match flow.await {
            Ok(result) => Ok(result),
            Err(err) => {
                // Terminal failure: shred before propagating. Idempotent with the success path's terminate, so
                // a failure after a partial terminate is harmless (the .NET termination no-ops on an absent key).
                self.gov_terminate(&ctx, &instance_id, "gov-terminate-fail").await?;
                Err(err)
            }
        }
    }

    async fn raise_event(
        &self,
        ctx: SharedWorkflowContext<'_>,
        req: Json<RaiseEvent>,
    ) -> Result<(), HandlerError> {
        let RaiseEvent { name, payload, .. } = req.into_inner();
        resolve_once(&ctx, name, payload).await?;
        Ok(())
    }
}

/// Build the HTTP client used to call the .NET step host. Plain HTTP works as-is (loopback default); when
/// STEP_URL is `https`, TLS is verified against the system/webpki roots. Set STEP_CA_CERT to a PEM file to
/// additionally trust a private CA (self-managed / internal PKI). A bad STEP_CA_CERT is fatal at startup
/// rather than silently falling back to an untrusted connection.
fn build_client() -> reqwest::Client {
    let mut builder = reqwest::Client::builder();
    if let Ok(ca_path) = std::env::var("STEP_CA_CERT") {
        let pem = std::fs::read(&ca_path)
            .unwrap_or_else(|e| panic!("STEP_CA_CERT '{ca_path}' is unreadable: {e}"));
        let cert = reqwest::Certificate::from_pem(&pem)
            .unwrap_or_else(|e| panic!("STEP_CA_CERT '{ca_path}' is not a valid PEM certificate: {e}"));
        builder = builder.add_root_certificate(cert);
    }
    builder.build().expect("failed to build the HTTP client")
}

#[tokio::main]
async fn main() {
    tracing_subscriber::fmt::init();
    let step_url = std::env::var("STEP_URL").unwrap_or_else(|_| "http://127.0.0.1:9090".to_string());
    // Bind loopback by default; set BIND explicitly to expose the sidecar beyond localhost.
    let bind = std::env::var("BIND").unwrap_or_else(|_| "127.0.0.1:9080".to_string());
    // Shared secret presented to the .NET step host on every call. Required: the host's
    // /step + /terminate (and the consumer's /gov-step + /gov-terminate) run governed steps and
    // crypto-shred, so they must never be reachable unauthenticated.
    let token = std::env::var("STEP_TOKEN")
        .expect("STEP_TOKEN must be set (shared secret for the .NET step host)");

    let generic = OnboardWorkflowImpl {
        client: build_client(),
        step_url: step_url.clone(),
        token: token.clone(),
    };
    let native = NativeOnboardWorkflowImpl {
        client: build_client(),
        step_url,
        token,
    };
    HttpServer::new(
        Endpoint::builder()
            .bind(generic.serve())
            .bind(native.serve())
            .build(),
    )
    .listen_and_serve(bind.parse().expect("invalid BIND address"))
    .await;
}

#[cfg(test)]
mod tests {
    use super::*;

    // --- parse_exec_key: instance id + continue-as-new sequence resume ---------------------------------

    #[test]
    fn exec_key_first_run_has_no_suffix_and_starts_at_zero() {
        assert_eq!(parse_exec_key("onboard-1"), ("onboard-1".to_string(), 0));
    }

    #[test]
    fn exec_key_carries_the_generation_sequence_after_the_tilde() {
        // continue-as-new appends ~<seq>; the logical id is the base, the sequence resumes from the suffix.
        assert_eq!(parse_exec_key("onboard-1~7"), ("onboard-1".to_string(), 7));
    }

    #[test]
    fn exec_key_keeps_the_base_id_before_the_first_tilde_only() {
        // Only the first '~' separates the suffix; anything after the second is ignored for the sequence.
        let (id, seq) = parse_exec_key("onboard-1~3~stray");
        assert_eq!(id, "onboard-1");
        assert_eq!(seq, 3);
    }

    #[test]
    fn exec_key_unparseable_suffix_falls_back_to_zero() {
        // A non-numeric suffix must not panic; it resumes at 0 rather than crashing the generation.
        assert_eq!(parse_exec_key("onboard-1~notanumber"), ("onboard-1".to_string(), 0));
    }

    // --- resume_or_fail: which envelope a wait resumes into ------------------------------------------

    #[test]
    fn resume_prefers_primary_when_present() {
        assert_eq!(resume_or_fail("raised".into(), "onevent".into(), "e", "missing").unwrap(), "raised");
    }

    #[test]
    fn resume_falls_back_to_event_step_when_primary_empty() {
        assert_eq!(resume_or_fail(String::new(), "onevent".into(), "e", "missing").unwrap(), "onevent");
    }

    #[test]
    fn resume_fails_descriptively_when_both_empty() {
        // An empty resume must error here, not continue with an empty envelope that only fails later at decrypt.
        assert!(resume_or_fail(String::new(), String::new(), "invite-accepted", "had no payload").is_err());
    }

    // --- intern_event_name: bounded, idempotent interning -------------------------------------------

    #[test]
    fn interning_the_same_name_returns_the_same_pointer() {
        let a = intern_event_name("invite-accepted".to_string()).unwrap();
        let b = intern_event_name("invite-accepted".to_string()).unwrap();
        assert!(std::ptr::eq(a, b), "the same event name must intern to one leaked &'static, not leak afresh");
        let c = intern_event_name("other-event".to_string()).unwrap();
        assert!(!std::ptr::eq(a, c));
        assert_eq!(c, "other-event");
    }

    #[test]
    fn interning_is_bounded_by_the_cap() {
        // Drive the testable core (its own HashSet, no global state) past a small cap: existing names always
        // resolve, but a NEW name past the cap fails rather than leaking without bound.
        let mut set = HashSet::new();
        assert!(intern_into(&mut set, "a".to_string(), 2).is_ok());
        assert!(intern_into(&mut set, "b".to_string(), 2).is_ok());
        // re-interning an already-present name is fine even at the cap (no new leak).
        assert!(intern_into(&mut set, "a".to_string(), 2).is_ok());
        // a new, distinct name past the cap is rejected.
        assert!(intern_into(&mut set, "c".to_string(), 2).is_err());
    }

    // --- ticks_to_duration: .NET-ticks → Duration, overflow-safe ------------------------------------

    #[test]
    fn ticks_convert_to_nanos_and_clamp_negatives() {
        assert_eq!(ticks_to_duration(6_000_000_000), Duration::from_nanos(600_000_000_000)); // 600s
        assert_eq!(ticks_to_duration(0), Duration::ZERO);
        assert_eq!(ticks_to_duration(-5), Duration::ZERO, "negative ticks clamp to zero");
    }

    #[test]
    fn ticks_to_duration_saturates_instead_of_overflowing() {
        // i64::MAX * 100 overflows u64; saturating_mul must yield a finite duration, not panic (debug) or
        // wrap to ~0 (release).
        let d = ticks_to_duration(i64::MAX);
        assert_eq!(d, Duration::from_nanos(u64::MAX), "huge ticks saturate to the max finite duration");
    }

    // --- ActionDto / RaiseEvent serde: shape the .NET host sends ------------------------------------

    #[test]
    fn action_dto_parses_camelcase_and_defaults_missing_fields() {
        let dto: ActionDto = serde_json::from_str(r#"{"kind":"wait","eventName":"invite-accepted","timeoutTicks":600,"onEvent":"c2VlZA=="}"#).unwrap();
        assert_eq!(dto.kind, "wait");
        assert_eq!(dto.event_name, "invite-accepted");
        assert_eq!(dto.timeout_ticks, 600);
        assert_eq!(dto.on_event, "c2VlZA==");
        assert_eq!(dto.payload, "", "absent byte[] fields default to empty, not an error");
        assert_eq!(dto.on_timeout, "");
    }

    #[test]
    fn action_dto_ignores_unknown_fields() {
        let dto: ActionDto = serde_json::from_str(r#"{"kind":"complete","payload":"b2s=","unknownField":42}"#).unwrap();
        assert_eq!(dto.kind, "complete");
        assert_eq!(dto.payload, "b2s=");
    }

    #[test]
    fn raise_event_parses_camelcase_with_optional_raise_id() {
        let with_id: RaiseEvent = serde_json::from_str(r#"{"name":"invite-accepted","payload":"cA==","raiseId":"r-1"}"#).unwrap();
        assert_eq!(with_id.name, "invite-accepted");
        assert_eq!(with_id.payload, "cA==");
        assert_eq!(with_id.raise_id.as_deref(), Some("r-1"));

        // raiseId is advisory and optional — a raise without it still parses.
        let without_id: RaiseEvent = serde_json::from_str(r#"{"name":"invite-accepted","payload":"cA=="}"#).unwrap();
        assert_eq!(without_id.raise_id, None);
    }

    // --- call_step / call_terminate: HTTP-callback transport + auth (integration, against a mock host) ----
    // These drive the real reqwest callback code over a real socket (wiremock), covering the sidecar's
    // transport/auth participation that the C# Restate Tier-2 tests otherwise exercise only end-to-end. The
    // durable-promise orchestration layer still relies on those C# tests — it needs the restate runtime.

    use wiremock::matchers::{body_string_contains, header, method, path};
    use wiremock::{Mock, MockServer, ResponseTemplate};

    #[tokio::test]
    async fn call_step_attaches_bearer_token_forwards_payload_verbatim_and_parses_the_action() {
        let server = MockServer::start().await;
        // The exact opaque sealed payload the sidecar must forward unchanged.
        let sealed = "U0VBTEVELXNlZWQtb3BhcXVl"; // base64 of an opaque "SEALED-seed-opaque"
        Mock::given(method("POST"))
            .and(path("/step"))
            .and(header("authorization", "Bearer test-token"))
            .and(body_string_contains(sealed))
            .respond_with(ResponseTemplate::new(200).set_body_json(serde_json::json!({
                "kind": "complete",
                "payload": "cmVzdWx0" // base64 "result"
            })))
            .expect(1)
            .mount(&server)
            .await;

        let client = reqwest::Client::new();
        let req = StepRequest { payload: sealed.to_string(), instance_id: "inst-1".to_string(), sequence: 0 };
        let dto = call_step(&client, &server.uri(), "test-token", &req).await.unwrap();

        assert_eq!(dto.kind, "complete");
        assert_eq!(dto.payload, "cmVzdWx0", "the host's action payload round-trips");

        // Verbatim/opaque forwarding: the body the host received carries the exact payload, unmodified.
        let received = &server.received_requests().await.unwrap()[0];
        let body: serde_json::Value = serde_json::from_slice(&received.body).unwrap();
        assert_eq!(body["payload"], sealed, "the sealed payload crossed verbatim — the sidecar did not touch it");
        assert_eq!(body["instanceId"], "inst-1");
    }

    #[tokio::test]
    async fn call_step_surfaces_a_non_2xx_response_as_an_error_not_a_panic() {
        let server = MockServer::start().await;
        Mock::given(method("POST")).and(path("/step"))
            .respond_with(ResponseTemplate::new(500))
            .mount(&server)
            .await;

        let client = reqwest::Client::new();
        let req = StepRequest { payload: "cA==".to_string(), instance_id: "inst-1".to_string(), sequence: 0 };
        let result = call_step(&client, &server.uri(), "test-token", &req).await;
        assert!(result.is_err(), "a 500 from the step host is an error, not a panic or a silent success");
    }

    #[tokio::test]
    async fn call_terminate_attaches_the_bearer_token() {
        let server = MockServer::start().await;
        Mock::given(method("POST"))
            .and(path("/terminate"))
            .and(header("authorization", "Bearer test-token"))
            .respond_with(ResponseTemplate::new(200))
            .expect(1)
            .mount(&server)
            .await;

        let client = reqwest::Client::new();
        let req = TerminateRequest { instance_id: "inst-1".to_string(), sequence: 1 };
        call_terminate(&client, &server.uri(), "test-token", &req).await.unwrap();
    }
}
