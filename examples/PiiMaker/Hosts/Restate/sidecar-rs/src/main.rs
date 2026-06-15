//! PiiMaker example — Restate sidecar (Rust).
//!
//! This is the EXAMPLE's own sidecar, separate from the library/test sidecar
//! (`src/SoEx.Workflow.Restate/restate-sidecar-rs`). It lets the example demonstrate the consumer-authored
//! native flows — including offboarding's parallel fan-out — without touching the shared sidecar the Tier-2
//! tests depend on. One binary exposes three Restate services on one endpoint:
//!
//!   - `MembershipPortable`  — the GENERIC portable flow: its `run` owns the durable step loop and maps the
//!     .NET manager's returned `WorkflowAction` (flattened to `ActionDto`) onto Restate's durable
//!     primitives. Drives any portable operation (onboarding, renewal). Calls `/step` + `/terminate`.
//!   - `MembershipOnboard`   — NATIVE onboarding: the consumer authors the flow here (lookup → create →
//!     reserve → invite → durable-promise wait → assign); .NET governs each step. Calls `/gov-step`.
//!   - `MembershipOffboard`  — NATIVE offboarding: the consumer fans out a governed revocation per
//!     downstream system IN PARALLEL, then shreds. The shape the sequential portable flow cannot express.
//!
//! Payloads are opaque base64 strings end to end. The native services call back to STEP_URL (default
//! http://127.0.0.1:9091); MembershipPortable calls back to PORTABLE_STEP_URL (defaults to STEP_URL) so a
//! host driving portable and native flows at once can run their two .NET callback hosts on separate ports.
//! The shared secret STEP_TOKEN is presented on every callback.

use restate_sdk::prelude::*;
use serde::{Deserialize, Serialize};
use std::collections::HashSet;
use std::sync::{Mutex, OnceLock};
use std::time::Duration;

/// Interns an event name to `'static`, leaking each DISTINCT name at most once. `ctx.promise(key)` binds the
/// key to the context lifetime and so needs `'static`; business event names are a small finite set, so the
/// interned set is bounded.
fn intern_event_name(name: String) -> &'static str {
    static INTERNED: OnceLock<Mutex<HashSet<&'static str>>> = OnceLock::new();
    let set = INTERNED.get_or_init(|| Mutex::new(HashSet::new()));
    let mut guard = set.lock().expect("interned event-name set poisoned");
    if let Some(existing) = guard.get(name.as_str()) {
        return existing;
    }
    let leaked: &'static str = Box::leak(name.into_boxed_str());
    guard.insert(leaked);
    leaked
}

/// The logical instance id (for keying/governance) is the base before any `~<gen>` continue-as-new suffix.
fn logical_instance(exec_key: &str) -> String {
    exec_key.split('~').next().unwrap_or(exec_key).to_string()
}

/// Deliver a raise idempotently — peek the write-once durable promise and skip a redundant re-resolve, so an
/// accidental redelivery cannot deliver the event twice. Mirrors the library sidecar's `resolve_once`; the bare
/// `resolve_promise` the example used before had no such guard.
async fn resolve_once(ctx: &SharedWorkflowContext<'_>, name: String, payload: String) -> Result<(), HandlerError> {
    let event: &'static str = intern_event_name(name);
    if ctx.peek_promise::<String>(event).await?.is_some() {
        return Ok(()); // already delivered — drop the re-raise
    }
    ctx.resolve_promise::<String>(event, payload);
    Ok(())
}

/// Pick the envelope to resume a wait into, or fail descriptively when there is none: `primary` (the raised
/// payload, or the on-timeout step) wins if non-empty, else `fallback` (the on-event step) if non-empty, else
/// a terminal error — so an empty resume does not continue with an empty envelope that fails later at decrypt.
fn resume_or_fail(primary: String, fallback: String, event_name: &str, what: &str) -> Result<String, HandlerError> {
    if !primary.is_empty() {
        Ok(primary)
    } else if !fallback.is_empty() {
        Ok(fallback)
    } else {
        Err(TerminalError::new(format!("'{event_name}' {what}")).into())
    }
}

/// Payload of a workflow's `raise_event` handler: `{ "name": <promise>, "payload": <base64> }`.
#[derive(Deserialize)]
struct RaiseEvent {
    name: String,
    payload: String,
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

/// Flattened `WorkflowAction` returned by `/step`. byte[] fields arrive as base64 strings; unknown JSON
/// fields are dropped by serde.
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
    #[serde(default)]
    on_timeout: String,
    /// The step to resume into when a `wait` signal arrives with an empty payload (base64
    /// envelope) — the flow's pre-sealed continuation; a non-empty raised payload wins.
    #[serde(default)]
    on_event: String,
}

#[restate_sdk::workflow]
#[name = "MembershipPortable"]
trait MembershipPortable {
    #[name = "run"]
    async fn run(seed: String) -> Result<String, HandlerError>;

    #[name = "raise_event"]
    #[shared]
    async fn raise_event(req: Json<RaiseEvent>) -> Result<(), HandlerError>;
}

struct MembershipPortableImpl {
    client: reqwest::Client,
    step_url: String,
    token: String,
}

impl MembershipPortable for MembershipPortableImpl {
    async fn run(&self, ctx: WorkflowContext<'_>, seed: String) -> Result<String, HandlerError> {
        let exec_key = ctx.key().to_string();
        let instance_id = logical_instance(&exec_key);
        let mut current = seed;
        // Resume the per-step sequence from the generation suffix (instance_id~<seq>) so continue-as-new
        // keeps the idempotency key (InstanceId, DtoType, Sequence) unique across generations; a first run
        // (no suffix) starts at 0.
        let mut sequence: i64 = exec_key.split('~').nth(1).and_then(|s| s.parse().ok()).unwrap_or(0);

        loop {
            let req = StepRequest { payload: current.clone(), instance_id: instance_id.clone(), sequence };
            let (client, url, token) = (self.client.clone(), self.step_url.clone(), self.token.clone());
            let dto: ActionDto = ctx
                .run(|| async move {
                    let resp = client
                        .post(format!("{url}/step"))
                        .bearer_auth(&token)
                        .json(&req)
                        .send()
                        .await?
                        .error_for_status()?;
                    Ok(Json(resp.json::<ActionDto>().await?))
                })
                .name(format!("step-{sequence}"))
                .await?
                .into_inner();
            sequence += 1;

            match dto.kind.as_str() {
                "complete" => {
                    let term = TerminateRequest { instance_id: instance_id.clone(), sequence };
                    let (client, url, token) = (self.client.clone(), self.step_url.clone(), self.token.clone());
                    ctx.run(|| async move {
                        client.post(format!("{url}/terminate")).bearer_auth(&token).json(&term).send().await?.error_for_status()?;
                        Ok(())
                    })
                    .name(format!("terminate-{sequence}"))
                    .await?;
                    return Ok(dto.payload);
                }
                "next" => current = dto.payload,
                "delay" => {
                    ctx.sleep(Duration::from_nanos((dto.timeout_ticks.max(0) as u64) * 100)).await?;
                }
                "wait" => {
                    let event: &'static str = intern_event_name(dto.event_name.clone());
                    let event_name = dto.event_name.clone();
                    if dto.timeout_ticks < 0 {
                        let resolved = ctx.promise::<String>(event).await?;
                        // an empty raise resumes into the pre-sealed on-event step; a payload wins. With neither
                        // there is nothing to resume into — fail descriptively rather than continue empty.
                        current = resume_or_fail(resolved, dto.on_event, &event_name, "was raised with an empty payload and the wait has no on-event step")?;
                    } else {
                        let dur = Duration::from_nanos((dto.timeout_ticks.max(0) as u64) * 100);
                        current = restate_sdk::select! {
                            resolved = ctx.promise::<String>(event) =>
                                resume_or_fail(resolved?, dto.on_event, &event_name, "was raised with an empty payload and the wait has no on-event step")?,
                            _ = ctx.sleep(dur) =>
                                resume_or_fail(dto.on_timeout, String::new(), &event_name, "timer elapsed with no on-timeout step")?,
                        };
                    }
                }
                "loop" => {
                    // continue-as-new: chain a fresh execution carrying state; id is instance_id~<absolute
                    // sequence> so the logical key is stable (carried) AND the next generation resumes the
                    // sequence — idempotency keys never collide across generations.
                    let next_id = format!("{instance_id}~{sequence}");
                    ctx.workflow_client::<MembershipPortableClient>(next_id).run(dto.payload).send();
                    return Ok(String::new());
                }
                other => {
                    // An unhandled action is a TERMINAL failure: crypto-shred before propagating so a
                    // terminally-failed instance doesn't retain its key — matching the library sidecar (the
                    // example previously errored without shredding).
                    let term = TerminateRequest { instance_id: instance_id.clone(), sequence };
                    let (client, url, token) = (self.client.clone(), self.step_url.clone(), self.token.clone());
                    ctx.run(|| async move {
                        client.post(format!("{url}/terminate")).bearer_auth(&token).json(&term).send().await?.error_for_status()?;
                        Ok(())
                    })
                    .name(format!("terminate-fail-{sequence}"))
                    .await?;
                    return Err(TerminalError::new(format!("unhandled action kind: {other}")).into());
                }
            }
        }
    }

    async fn raise_event(&self, ctx: SharedWorkflowContext<'_>, req: Json<RaiseEvent>) -> Result<(), HandlerError> {
        let RaiseEvent { name, payload } = req.into_inner();
        resolve_once(&ctx, name, payload).await?;
        Ok(())
    }
}

// ===========================================================================================
// Native flows — the consumer authors the flow here; .NET governs each step + termination.
// ===========================================================================================

/// Request to the .NET governed step host `/gov-step`. `kind` is the step discriminator (an onboarding step
/// name, or — for offboarding — the target system); `data` is the opaque sealed seed (base64).
#[derive(Serialize, Deserialize)]
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

/// One governed step: POST `/gov-step` (journalled durably under `name`). The closure owns its inputs.
fn gov_step<'ctx>(
    ctx: &'ctx WorkflowContext<'ctx>,
    client: reqwest::Client,
    url: String,
    token: String,
    req: GovStepRequest,
    name: String,
) -> impl std::future::Future<Output = Result<(), TerminalError>> + 'ctx {
    ctx.run(move || async move {
        client.post(format!("{url}/gov-step")).bearer_auth(&token).json(&req).send().await?.error_for_status()?;
        Ok(())
    })
    .name(name)
}

async fn gov_terminate(ctx: &WorkflowContext<'_>, client: reqwest::Client, url: String, token: String, instance_id: String) -> Result<(), TerminalError> {
    let term = GovTerminateRequest { instance_id };
    ctx.run(move || async move {
        client.post(format!("{url}/gov-terminate")).bearer_auth(&token).json(&term).send().await?.error_for_status()?;
        Ok(())
    })
    .name("gov-terminate")
    .await
}

// ---- native onboarding --------------------------------------------------------------------

#[restate_sdk::workflow]
#[name = "MembershipOnboard"]
trait MembershipOnboard {
    #[name = "run"]
    async fn run(seed: String) -> Result<String, HandlerError>;

    #[name = "raise_event"]
    #[shared]
    async fn raise_event(req: Json<RaiseEvent>) -> Result<(), HandlerError>;
}

struct MembershipOnboardImpl {
    client: reqwest::Client,
    step_url: String,
    token: String,
}

impl MembershipOnboard for MembershipOnboardImpl {
    async fn run(&self, ctx: WorkflowContext<'_>, seed: String) -> Result<String, HandlerError> {
        let instance_id = logical_instance(&ctx.key().to_string());

        // each governed step: a call to .NET, journalled durably (only the sealed seed travels).
        for (seq, kind) in [(0i64, "lookup"), (1, "create"), (2, "reserve"), (3, "invite")] {
            let req = GovStepRequest { instance_id: instance_id.clone(), seq, kind: kind.to_string(), data: seed.clone() };
            gov_step(&ctx, self.client.clone(), self.step_url.clone(), self.token.clone(), req, format!("gov-step-{seq}")).await?;
        }

        // native durable-promise wait — the flow lives here, in the sidecar.
        let _: String = ctx.promise::<String>("invite-accepted").await?;

        let req = GovStepRequest { instance_id: instance_id.clone(), seq: 4, kind: "assign".to_string(), data: seed.clone() };
        gov_step(&ctx, self.client.clone(), self.step_url.clone(), self.token.clone(), req, "gov-step-4".to_string()).await?;

        gov_terminate(&ctx, self.client.clone(), self.step_url.clone(), self.token.clone(), instance_id).await?;
        Ok("assigned".to_string())
    }

    async fn raise_event(&self, ctx: SharedWorkflowContext<'_>, req: Json<RaiseEvent>) -> Result<(), HandlerError> {
        let RaiseEvent { name, payload } = req.into_inner();
        resolve_once(&ctx, name, payload).await?;
        Ok(())
    }
}

// ---- the per-system revocation, as its own service so the fan-out can run in PARALLEL ------
// Restate parallelism is durable *calls*, not concurrent side-effects (the SDK requires `ctx.run` to be
// awaited immediately). So each revocation is a service call the offboarding workflow issues in parallel
// and joins; this handler performs the one governed step.

#[restate_sdk::service]
#[name = "MembershipRevoke"]
trait MembershipRevoke {
    #[name = "revoke"]
    async fn revoke(req: Json<GovStepRequest>) -> Result<(), HandlerError>;
}

struct MembershipRevokeImpl {
    client: reqwest::Client,
    step_url: String,
    token: String,
}

impl MembershipRevoke for MembershipRevokeImpl {
    async fn revoke(&self, ctx: Context<'_>, req: Json<GovStepRequest>) -> Result<(), HandlerError> {
        let req = req.into_inner();
        let (client, url, token) = (self.client.clone(), self.step_url.clone(), self.token.clone());
        ctx.run(move || async move {
            client.post(format!("{url}/gov-step")).bearer_auth(&token).json(&req).send().await?.error_for_status()?;
            Ok(())
        })
        .name("gov-step")
        .await?;
        Ok(())
    }
}

// ---- native offboarding (parallel fan-out) ------------------------------------------------

#[restate_sdk::workflow]
#[name = "MembershipOffboard"]
trait MembershipOffboard {
    #[name = "run"]
    async fn run(seed: String) -> Result<String, HandlerError>;
}

struct MembershipOffboardImpl {
    client: reqwest::Client,
    step_url: String,
    token: String,
}

impl MembershipOffboard for MembershipOffboardImpl {
    async fn run(&self, ctx: WorkflowContext<'_>, seed: String) -> Result<String, HandlerError> {
        let instance_id = logical_instance(&ctx.key().to_string());
        let systems = ["mail", "vpn", "billing-portal", "wiki"];

        // fan out: one governed revocation per downstream system, IN PARALLEL — each is a durable call to
        // the MembershipRevoke service, joined as they complete. `kind` carries the target system.
        let revoker = ctx.service_client::<MembershipRevokeClient>();
        let mut calls: DurableFuturesUnordered<_> = systems
            .iter()
            .enumerate()
            .map(|(i, system)| {
                let req = GovStepRequest { instance_id: instance_id.clone(), seq: i as i64, kind: system.to_string(), data: seed.clone() };
                revoker.revoke(Json(req)).call()
            })
            .collect();
        while let Some((_, result)) = calls.next().await? {
            result?;
        }

        // termination hook: crypto-shred once every revocation has completed.
        gov_terminate(&ctx, self.client.clone(), self.step_url.clone(), self.token.clone(), instance_id).await?;
        Ok("offboarded".to_string())
    }
}

#[tokio::main]
async fn main() {
    tracing_subscriber::fmt::init();
    let step_url = std::env::var("STEP_URL").unwrap_or_else(|_| "http://127.0.0.1:9091".to_string());
    // The portable flow calls its own /step+/terminate host; the native services call /gov-step. A host that
    // drives them concurrently (the web control panel) keeps the two .NET callback hosts on separate ports, so
    // PORTABLE_STEP_URL overrides where MembershipPortable calls back (defaults to STEP_URL when unset, which
    // is how the single-flow-at-a-time scripted host runs).
    let portable_step_url = std::env::var("PORTABLE_STEP_URL").unwrap_or_else(|_| step_url.clone());
    let bind = std::env::var("BIND").unwrap_or_else(|_| "127.0.0.1:9081".to_string());
    let token = std::env::var("STEP_TOKEN").expect("STEP_TOKEN must be set (shared secret for the .NET step host)");

    let portable = MembershipPortableImpl { client: reqwest::Client::new(), step_url: portable_step_url, token: token.clone() };
    let onboard = MembershipOnboardImpl { client: reqwest::Client::new(), step_url: step_url.clone(), token: token.clone() };
    let offboard = MembershipOffboardImpl { client: reqwest::Client::new(), step_url: step_url.clone(), token: token.clone() };
    let revoke = MembershipRevokeImpl { client: reqwest::Client::new(), step_url, token };

    HttpServer::new(
        Endpoint::builder()
            .bind(portable.serve())
            .bind(onboard.serve())
            .bind(offboard.serve())
            .bind(revoke.serve())
            .build(),
    )
    .listen_and_serve(bind.parse().expect("invalid BIND address"))
    .await;
}
