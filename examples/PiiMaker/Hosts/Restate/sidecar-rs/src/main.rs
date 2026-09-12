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

use restate_sdk::context::macro_support::SealedDurableFuture;
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

/// Resolve what a wait resumes into once a branch's promise settles, merging the branch's pre-sealed
/// `on_event` continuation with any payload the raiser sent. Returns `(next step payload, event data)`;
/// event data is opaque to the sidecar, exactly like `payload`, and travels to exactly one following
/// `/step` call (the caller clears it after that call). Mirrors the library sidecar's `resolve_resume`:
///
/// | branch has `on_event` | raise payload | next step   | event data |
/// |---|---|---|---|
/// | yes | non-empty | `on_event`  | the payload |
/// | yes | empty     | `on_event`  | none |
/// | no  | non-empty | the payload | none |
/// | no  | empty     | — (terminal error: nothing to resume into) |
fn resolve_resume(resolved: String, on_event: String, event_name: &str) -> Result<(String, String), HandlerError> {
    if !on_event.is_empty() {
        Ok((on_event, resolved))
    } else if !resolved.is_empty() {
        Ok((resolved, String::new()))
    } else {
        Err(TerminalError::new(format!(
            "'{event_name}' was raised with an empty payload and its branch of the wait has no on-event step"
        ))
        .into())
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
///
/// `event_data` is ALWAYS present (never omitted, never `null`) — an empty string when a step carries
/// none. It must stay a plain `String`, not `Option<String>`: a nullable/absent field here decodes as a
/// type error on the .NET side and fails every action's invocation, not just the one that omitted it.
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct StepRequest {
    payload: String,
    instance_id: String,
    sequence: i64,
    event_data: String,
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
/// The wire-contract version this sidecar speaks, sent on every portable callback so the .NET host can
/// refuse a stale binary running an old contract. Must match `RestateWorkflowHost.WireVersion`; bump both
/// in lock-step on any breaking change to the /step or /terminate shapes.
///
/// Version 3 added `StepRequest.event_data` (always-present, empty-string-when-absent): a wait resumed
/// into a branch's `on_event` step now carries the raiser's non-empty payload separately as event data
/// instead of discarding it. See `resolve_resume`.
const WIRE_VERSION: &str = "3";
const WIRE_VERSION_HEADER: &str = "x-soex-wire-version";

/// One branch of a `wait`: the event name that resumes it, plus that branch's pre-sealed on-event step
/// (base64 envelope), empty when the branch declared none.
#[derive(Serialize, Deserialize, Clone)]
#[serde(rename_all = "camelCase")]
struct WaitBranch {
    event_name: String,
    #[serde(default)]
    on_event: String,
}

/// The branches a `wait` can be resumed by. A wait journaled before branches existed carries only the
/// legacy `eventName`/`onEvent` pair, so it reads back as the one-branch wait it is.
fn wait_branches(dto: &ActionDto) -> Vec<WaitBranch> {
    let declared = dto.branches.as_deref().unwrap_or(&[]);
    if declared.is_empty() {
        vec![WaitBranch { event_name: dto.event_name.clone(), on_event: dto.on_event.clone() }]
    } else {
        declared.to_vec()
    }
}

/// Renders a wait's branch names for a diagnostic: `'a'`, or `'a', 'b'` for a multi-branch wait.
fn quoted_names(branches: &[WaitBranch]) -> String {
    branches.iter().map(|b| format!("'{}'", b.event_name)).collect::<Vec<_>>().join(", ")
}

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
    /// envelope) — the flow's pre-sealed continuation; a non-empty raised payload wins. Carries
    /// branch 0 of `branches`, so a single-branch wait journaled here still replays after a rollback.
    #[serde(default)]
    on_event: String,
    /// The branches a `wait` can be resumed by, in declared (tie-break) order. `Option` rather than a
    /// bare `Vec` so BOTH an absent field (a host deployed before branches) and an explicit JSON `null`
    /// decode: `serde(default)` alone covers only the absent case, and a null would fail the whole
    /// invocation with a decode error rather than degrade to no branches.
    #[serde(default)]
    branches: Option<Vec<WaitBranch>>,
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
        // The raiser's payload when a wait resumed into a branch's on-event step (see `resolve_resume`);
        // empty otherwise. Carried to exactly the next `/step` call, then cleared — one dispatch only. Never
        // crosses a continue-as-new boundary: the next generation's run() starts its own at "".
        let mut event_data = String::new();

        loop {
            let req = StepRequest { payload: current.clone(), instance_id: instance_id.clone(), sequence, event_data: event_data.clone() };
            let (client, url, token) = (self.client.clone(), self.step_url.clone(), self.token.clone());
            let dto: ActionDto = ctx
                .run(|| async move {
                    let resp = client
                        .post(format!("{url}/step"))
                        .bearer_auth(&token)
                        .header(WIRE_VERSION_HEADER, WIRE_VERSION)
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
            event_data = String::new(); // consumed by exactly the /step call above

            match dto.kind.as_str() {
                "complete" => {
                    let term = TerminateRequest { instance_id: instance_id.clone(), sequence };
                    let (client, url, token) = (self.client.clone(), self.step_url.clone(), self.token.clone());
                    ctx.run(|| async move {
                        client.post(format!("{url}/terminate")).bearer_auth(&token).header(WIRE_VERSION_HEADER, WIRE_VERSION).json(&term).send().await?.error_for_status()?;
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
                    // Every branch parks on its own durable promise; they race each other AND the timer.
                    // `select!` is fixed-arity and the branch count is only known at run time, so the race
                    // is built the way the SDK's own DurableFuturesUnordered::next builds it: one handle
                    // per future, runtime picks the first to complete. Handle order is branch order with
                    // the timer last, so the winning index maps back to a branch.
                    let branches = wait_branches(&dto);
                    let mut promises = Vec::with_capacity(branches.len());
                    for branch in &branches {
                        let event: &'static str = intern_event_name(branch.event_name.clone());
                        promises.push(ctx.promise::<String>(event));
                    }

                    let timer = if dto.timeout_ticks < 0 {
                        None
                    } else {
                        Some(ctx.sleep(Duration::from_nanos((dto.timeout_ticks.max(0) as u64) * 100)))
                    };

                    let mut handles: Vec<_> = promises.iter().map(|p| p.handle()).collect();
                    if let Some(timer) = &timer {
                        handles.push(timer.handle());
                    }
                    let winner = promises[0].inner_context().select(handles).await?;

                    current = if winner < promises.len() {
                        let branch = branches[winner].clone();
                        let resolved = promises.swap_remove(winner).await?;
                        // A raised branch resumes into that branch's pre-sealed on-event step when it has
                        // one, carrying any non-empty payload separately as event data; with no on-event
                        // step the payload becomes the next step itself.
                        let (next, data) = resolve_resume(resolved, branch.on_event.clone(), &branch.event_name)?;
                        event_data = data;
                        next
                    } else {
                        // Await the winner even when it is the timer: select() reports WHICH future
                        // completed, and the SDK's own combinators then await it to consume the completion.
                        timer.expect("a timer index can only win when a timer was armed").await?;
                        if dto.on_timeout.is_empty() {
                            return Err(TerminalError::new(format!(
                                "durable timer elapsed waiting for {} with no on-timeout step", quoted_names(&branches))).into());
                        }

                        // on-timeout never carries event data (only a raised event can).
                        dto.on_timeout.clone()
                    };
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
                        client.post(format!("{url}/terminate")).bearer_auth(&token).header(WIRE_VERSION_HEADER, WIRE_VERSION).json(&term).send().await?.error_for_status()?;
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

#[cfg(test)]
mod tests {
    use super::*;

    // --- resolve_resume: the merged on-event/event-data resolution table. Mirrors the coverage in the
    // library sidecar (src/SoEx.Workflow.Runtime.Restate/restate-sidecar-rs) for the same logic. ---------

    #[test]
    fn on_event_branch_with_a_non_empty_raise_resumes_into_on_event_carrying_the_payload_as_event_data() {
        let (next, data) = resolve_resume("raised".into(), "onevent".into(), "e").unwrap();
        assert_eq!(next, "onevent");
        assert_eq!(data, "raised");
    }

    #[test]
    fn on_event_branch_with_an_empty_raise_resumes_into_on_event_with_no_event_data() {
        let (next, data) = resolve_resume(String::new(), "onevent".into(), "e").unwrap();
        assert_eq!(next, "onevent");
        assert_eq!(data, "");
    }

    #[test]
    fn no_on_event_branch_with_a_non_empty_raise_resumes_into_the_payload_with_no_event_data() {
        let (next, data) = resolve_resume("raised".into(), String::new(), "e").unwrap();
        assert_eq!(next, "raised");
        assert_eq!(data, "");
    }

    #[test]
    fn no_on_event_branch_with_an_empty_raise_fails_descriptively() {
        assert!(resolve_resume(String::new(), String::new(), "invite-accepted").is_err());
    }
}
