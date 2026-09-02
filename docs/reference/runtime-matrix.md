> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Reference — runtime matrix

How the SoEx model maps to each runtime, and where their trigger semantics diverge. The governed core
is identical on every runtime (the same pipeline, key mint, subject index, idempotency, and
termination lifecycle); only the flow and the engine's edge behaviors differ. For the reasoning see
[Runtimes and durability](../explanation/runtimes-and-durability.md).

**Guard coverage (native vs portable).** The portable flow controls every runtime-visible surface, so
it guards them all automatically: the instance id, each step result, and the flow's own wait/timer
names and final return value. In the native model the consumer authors the flow, so the framework only
guards what flows through `GovernedStep`. The instance id and each step result are guarded on every
runtime, but the orchestration's own return value and any consumer-chosen wait/event name are the
consumer's duty: keep them PII-free, or pass them through `IGovernedStep.GuardVisibleName(...)` (the
same chokepoint the drivers use, and the one the Zeebe host applies to its job/incident names).
Likewise keep PII out of your own step exception messages. The framework scrubs a known subject from a
failure message before it reaches durable backend state, but that is a substring safety net, not full
PII detection.

One piece of *automated* tooling for that consumer duty exists on only one runtime: Camunda 8 / Zeebe.
Because a BPMN flow is a declarative artifact, `ZeebeWorkflowHost.ValidateResource` (run at deploy time)
lints the diagram's io-mappings and warns if a service task copies a framework-owned variable (`seed`/
`instanceId`) into a journaled variable under an unguarded name. The other native runtimes author the flow
in imperative code with no equivalent declarative surface to scan and no replay-deterministic seam at which
to guard the flow's own return value, so there is no equivalent deploy-time lint — the `GovernedStep` guards
(instance id + each step result, on every runtime) still apply, and the rest is consumer discipline. Porting
a flow off Zeebe therefore loses the deploy-time warning, not the `GovernedStep` guards. See the
*Native PII-guard tooling* row below.

## How the model maps (native flow)

A native flow has no `WorkflowAction`, so the portable flow's `.Enrolling(...)` seam does not apply: the
flow author owns the `StepContext` and therefore owns the ambient. On the **portable** flow, enrolling a
subject a step learned behaves identically on all five runtimes — each driver folds the declared subjects
in before it flattens the action, so the subject rides the sealed continuation and never reaches the
journal. It is covered by the cross-runtime conformance suite rather than listed per engine here.

| Concept | DTFx | Temporal | Elsa | Restate | Camunda 8 / Zeebe |
|---|---|---|---|---|---|
| **Flow (consumer-authored)** | `GovernedTaskOrchestrator.Flow` (CallActivity + WaitForExternalEvent) | `[Workflow]` (ExecuteActivity + WaitConditionAsync) | registered Elsa workflow (activities + bookmarks) | Restate sidecar (`ctx.run` + durable promise) | BPMN diagram (service tasks + message-catch events), broker-owned |
| **Governed step** | step activity → `GovernedStep.ExecuteAsync` | `[Activity]` → same | activity → same | `POST /gov-step` → same | service-task job worker → same |
| **Step dispatch** | `WorkflowEndpoint<I>` → `EndpointPipeline.ServicePipeLine<I>` → `DefaultDispatcher` → `component.<op>(typedDto)` | ← same | ← same | ← same (over HTTP) | ← same (via the job worker) |
| **Termination hook** | base orchestrator → `GovernedTerminationActivity` | `GovernedTerminationInterceptor` → termination activity | `GovernedTerminationActivity` | `POST /gov-terminate` → `GovernedTermination` | process end execution-listener job → `GovernedTermination` |
| **Durability model** | event-sourced replay | event-sourced replay | checkpoint/resume (bookmarks) | journalled (out-of-process sidecar) | broker-journalled (process variables) |
| **Native PII-guard tooling** | none — consumer duty + `GovernedStep` guards | none — same | none — same | none — same | deploy-time BPMN io-mapping lint (`ValidateResource`) |
| **Subject learned mid-flow** | consumer duty: build a fresh `SubjectContext.Managed(...)` ambient and pass it on the `StepContext` | ← same | ← same | ← same | ← same |

The invariant is in the *Step dispatch* row: the component is invoked the same way on every runtime,
by the SoEx endpoint pipeline, so your step code is identical and only the flow around it changes.

## Availability

| Runtime | Native flow | Portable flow |
|---|---|---|
| InProc | — (no native backend) | Yes (always portable) |
| Durable Task | Yes | Yes |
| Temporal | Yes | Yes |
| Elsa | Yes | Yes (durable timers need a consumer-driven resumer — see below) |
| Restate | Yes | Yes |
| Camunda 8 / Zeebe | Yes | — (native-only) |

> **Elsa portable durable timers need a consumer-driven resumer.** On every other engine a portable
> `WorkflowAction.Delay` or a wait-with-timeout fires on the engine's own clock. Elsa is the exception: the
> driver suspends on a `__timer` bookmark that carries its due time (`dueAt`) and the sealed step to resume
> into, but the framework does not host a scheduler for Elsa, so nothing fires it in a bare Elsa deployment.
> A production Elsa host must run a background resumer that scans due `__timer` bookmarks and resumes them (or
> wire Elsa's own scheduling feature). The building block ships — the bookmark records everything a resumer
> needs — but hosting the resumer is the consumer's job; until it is wired, a portable timer on Elsa parks
> indefinitely. Prefer Temporal for a portable flow that leans on durable timers.

## Gateway semantics

The `IWorkflowGateway` interface is uniform and the happy path matches on every engine (a shared
gateway-conformance suite in the private test repo asserts identical start→raise behavior across all
adapters, including duplicate-start rejection). A duplicate start of a live id raises the same
`WorkflowInstanceAlreadyExistsException` on every adapter that can detect it, so a caller catches one type
regardless of engine (the example surfaces it as an HTTP 409 rather than a raw fault). One edge still
diverges and one engine cannot detect the duplicate at all, so design your caller for the engine you target.

| Behavior | InProc | Durable Task | Temporal | Elsa | Restate | Zeebe |
|---|---|---|---|---|---|---|
| **Duplicate start** (same id twice) | `WorkflowInstanceAlreadyExistsException` while running; a completed id frees and can be re-onboarded | `WorkflowInstanceAlreadyExistsException` while live; a completed id frees | `WorkflowInstanceAlreadyExistsException` (from `WorkflowAlreadyStarted`) | `WorkflowInstanceAlreadyExistsException` (a run already holds the correlation) | `WorkflowInstanceAlreadyExistsException` (a key runs once ever, so a completed key stays taken) | plain `StartAsync`: **not detectable** — the broker mints its own key, so start-idempotency is the caller's duty; `StartByMessageAsync` dedupes by message id within a TTL |
| **Raise before the wait is armed** | buffered | buffered | buffered (durable signal) | rejected (no bookmark yet) | resolved into the promise when the wait arms | broker-correlated (message TTL) |
| **Multi-branch wait** (several named events racing the timer) | all branches parked at once; declared-order tie-break | one external-event receiver per branch | one signal name per branch, checked in declared order | one bookmark per branch; the rest are burned on resume | one durable promise per branch, raced together; write-once per name per generation | portable flow not available (native BPMN only) |
| **Idempotent raise** (`raiseId`) | dedupes (per-instance handled-id set, instance-lifetime) | dedupes (portable flow; per-generation — the set resets across continue-as-new) | dedupes (portable flow; per-generation — resets across continue-as-new) | dedupes when an `IIdempotencyStore` is wired, else `NotSupportedException` | deduped by construction (write-once promise; `raiseId` advisory) | dedupes via broker message id within TTL |

Practical consequences:

- On InProc a completed id can be re-onboarded as a fresh generation; on Restate a key runs once ever.
  Don't assume one rule across engines.
- On Elsa, make sure the wait is armed before you raise (or use a payload-carrying raise / retry), and
  wire an `IIdempotencyStore` if you need idempotent raises.
- A re-raise of an already-handled event re-executes its `OnEvent` continuation under a fresh sequence.
  It is not deduplicated by event name, because two raises of one name are two business events; use a
  `raiseId` to make a specific raise idempotent.
- On Temporal and Durable Task the `raiseId` dedup set is per generation: it resets across
  continue-as-new, so a retried raise that straddles a `Loop` (CAN) boundary can deliver twice. If a
  raise must be exactly-once across a CAN boundary, gate it on a durable effect rather than the
  in-memory set.
- The two engines differ at the CAN boundary in opposite directions. Durable Task continues-as-new with
  `preserveUnprocessedEvents: false` — a raise that lands in the continue-as-new transition window is
  **dropped**, not carried into the next generation. This is deliberate: carrying buffered events forward would
  reset the per-generation dedup semantics. If a raise near a `Loop` must not be lost, make it re-drivable
  (re-raise until the flow acknowledges it) rather than relying on it being buffered across the boundary.
- For single-active start on Zeebe, use `StartByMessageAsync` (TTL-bounded broker dedup); plain `StartAsync`
  has no duplicate-start protection, so start from a `DeterministicInstanceId` and gate re-entry at the seam.
  Elsa has the same hazard on a plain start by correlation id — two live instances of one logical id share one
  key, and the first to terminate shreds the other's live data — so gate the single-active start there too.
- A multi-branch wait behaves the same on every runtime that has the portable flow, with one exception.
  On Restate a durable promise is write-once per event NAME for the life of a generation, so a branch
  that can be raised more than once (a resend button, say) delivers only its first raise unless the flow
  takes a `Loop` after handling it, which starts a fresh generation with fresh promises. The other
  engines consume the delivery and re-arm, so a repeated raise at one branch just works.
- On Elsa, a raise that arrives for a branch after another branch has already resumed the wait is
  rejected rather than buffered, because the bookmarks are burned on resume. That is the same
  raise-before-the-wait-is-armed behavior as the row above, and it is loud rather than silent.
- The Zeebe raise TTL (how long the broker buffers a message before it is silently dropped if it never
  correlates) defaults to 5 minutes and is now settable on the gateway (`raiseTtl`); size it to your worst-case
  arm-the-wait latency for a slow-arming flow.

## In-flight evolution

What happens to instances that were already running when you redeploy a changed flow. The full reasoning
and the store-free pin-and-drain pattern are in [Versioning and evolution](../explanation/versioning-and-evolution.md);
this is the per-runtime summary.

| Runtime | Portable flow | Native flow | Tool for a breaking change |
|---|---|---|---|
| InProc | no durability across a restart, so no in-flight question | — | not applicable |
| Durable Task | safe (step is an activity); in-flight instances roll forward | orchestrator is replayed, no in-code patch API | new orchestration name for the new version; drain the old |
| Temporal | safe (step is an activity); in-flight instances roll forward | `[Workflow]` is replayed; a control-flow change can throw a non-determinism error | `Workflow.Patched` / `GetVersion`, or Worker Build-ID versioning |
| Elsa | definitions versioned natively; running instances stay pinned | ← same | publish a new definition version; pin a specific version to hold new starts back |
| Restate | deployments versioned natively; in-flight invocations stay pinned | ← same | deploy a new sidecar deployment |
| Camunda 8 / Zeebe | — (native-only) | BPMN definitions versioned natively; running instances stay pinned | new BPMN version, or Camunda process-instance migration |

The portable flow rolls forward everywhere it runs, because your step code is off the replay path, so a
backward-compatible change needs only a redeploy. Where you need old instances never to meet new code,
give the new version its own instance-id space with a version token in the `DeterministicInstanceId`
prefix and drain the old one. See [Evolve a running flow](../how-to/evolve-a-running-flow.md) for the
recipe.

## Step failure, retry, and poison

What happens when a governed step throws. The framework applies one cross-runtime default — a bounded
retry, then **park-before-shred** — so failure behavior no longer diverges by engine, and a transient
failure can never destroy the sealed journal. `WorkflowStepOptions` (max attempts, backoff, per-step
timeout, terminal-exception predicate) is the seam; each adapter maps it onto its engine's native retry
primitive, and every driver shares one park path (`GovernedTermination.QuarantineAsync`).

The failure path and the erasure path are distinct. A failing step is **retried** up to the bound; when
the attempts are spent — or the failure is classified terminal — the instance is **parked**: its key is
**retained** (recorded in the held registry, `OnRetentionHeld` fired), *not* crypto-shredded. Recovery is
an audited re-drive (resume) or a deliberate terminate (which then shreds). The key is destroyed only on a
deliberate termination — a natural completion, or an erasure/force-terminate through the coordinator — never
on the failure path.

| Behavior | InProc | Durable Task | Temporal | Elsa | Restate | Zeebe |
|---|---|---|---|---|---|---|
| **Retry** | driver loop (bounded exponential backoff) | activity `RetryPolicy` (`TaskOptions`) | activity `RetryPolicy` | driver loop | sidecar retry (see caveat) | broker job retries, decremented per failure |
| **On retries exhausted** | park (key retained, held) | park (quarantine activity) | park (quarantine activity) | park (key retained, held) | incident (see caveat) | broker incident (key retained) |
| **Default** | 3 attempts, 1s → 2s backoff | ← same | ← same | ← same | see caveat | BPMN task `retries`, then incident |
| **Per-binding tuning** | `WorkflowStepOptions` on the driver | static default in the orchestration (SDK-constructed) | ← same | `WorkflowStepOptions` on the activity | sidecar config | BPMN `retries` attribute |

The old destructive defaults are gone: Durable Task and Elsa used to crypto-shred the instance key on the
**first** failure (a transient DB blip permanently erased the flow); Temporal had no retry policy, so the
server default retried a failing step **forever, silently**. Both are now bounded-retry-then-park.

> **Restate caveat.** The Rust sidecar still retries a failing step with infinite backoff (an HTTP 500 is
> retried by the Restate runtime) and does not yet drive the park path. Bounding the sidecar retry and
> wiring park-before-shred there is a tracked follow-up; until then, on Restate a poison step retries
> indefinitely rather than parking. Wire external stuck-instance alerting on the Restate leg.

> **Per-binding options on the replay engines.** On Durable Task and Temporal the orchestration/workflow is
> SDK-constructed on the replay path, so it reads the retry policy from a static default rather than a
> per-binding `WorkflowStepOptions`. Tune the default centrally; per-binding overrides on those two engines
> are a follow-up. The in-process and Elsa drivers take per-binding options directly.

## Verifying locally

Each runtime is exercised against its backend, so full verification depends on those backends being
up. A run cannot quietly under-report its coverage, though. The suite is split into a hermetic set and
a backend-bound set. The hermetic set (in-memory, Temporal's time-skipping environment, and Elsa over
SQLite) needs no infrastructure and is what a plain run executes; it is genuinely green on a bare
machine, with no skipped cases hiding behind the result. Every test that needs a real backend is opt-in
and selected by category, so a run states which backends it covered by the filter it used, and a
selected backend test that cannot reach its backend fails rather than skipping. In other words, there
is no silent-skip path that lets a green run certify less than it appears to: either a test was not
selected (and is absent from the run), or it was selected and had to prove itself against a live
backend. A failed Restate sidecar build is likewise a hard failure rather than a skip, since a
present-but-broken sidecar certifies nothing. Bring up the backends listed above (and OpenBao for the
key-store leg) before selecting their tests.

For the full setup and the timing traps that otherwise produce false results, see
[Verify it yourself](../how-to/verify-it-yourself.md).

## See also

- [Triggering reference](triggering.md) — the gateway, sealer, and id types.
- [Author a native flow](../how-to/author-a-native-flow.md) — the per-runtime recipes.
- [Versioning and evolution](../explanation/versioning-and-evolution.md) — the reasoning behind the
  in-flight evolution summary above.
