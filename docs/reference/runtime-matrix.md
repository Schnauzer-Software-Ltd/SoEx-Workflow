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

| Concept | DTFx | Temporal | Elsa | Restate | Camunda 8 / Zeebe |
|---|---|---|---|---|---|
| **Flow (consumer-authored)** | `GovernedTaskOrchestrator.Flow` (CallActivity + WaitForExternalEvent) | `[Workflow]` (ExecuteActivity + WaitConditionAsync) | registered Elsa workflow (activities + bookmarks) | Restate sidecar (`ctx.run` + durable promise) | BPMN diagram (service tasks + message-catch events), broker-owned |
| **Governed step** | step activity → `GovernedStep.ExecuteAsync` | `[Activity]` → same | activity → same | `POST /gov-step` → same | service-task job worker → same |
| **Step dispatch** | `WorkflowEndpoint<I>` → `EndpointPipeline.ServicePipeLine<I>` → `DefaultDispatcher` → `component.<op>(typedDto)` | ← same | ← same | ← same (over HTTP) | ← same (via the job worker) |
| **Termination hook** | base orchestrator → `GovernedTerminationActivity` | `GovernedTerminationInterceptor` → termination activity | `GovernedTerminationActivity` | `POST /gov-terminate` → `GovernedTermination` | process end execution-listener job → `GovernedTermination` |
| **Durability model** | event-sourced replay | event-sourced replay | checkpoint/resume (bookmarks) | journalled (out-of-process sidecar) | broker-journalled (process variables) |
| **Native PII-guard tooling** | none — consumer duty + `GovernedStep` guards | none — same | none — same | none — same | deploy-time BPMN io-mapping lint (`ValidateResource`) |

The invariant is in the *Step dispatch* row: the component is invoked the same way on every runtime,
by the SoEx endpoint pipeline, so your step code is identical and only the flow around it changes.

## Availability

| Runtime | Native flow | Portable flow |
|---|---|---|
| InProc | — (no native backend) | Yes (always portable) |
| Durable Task | Yes | Yes |
| Temporal | Yes | Yes |
| Elsa | Yes | Yes |
| Restate | Yes | Yes |
| Camunda 8 / Zeebe | Yes | — (native-only) |

## Gateway semantics

The `IWorkflowGateway` interface is uniform and the happy path matches on every engine (a shared
gateway-conformance suite in the private test repo asserts identical start→raise behavior across all
adapters). Two edge behaviors diverge, so design your caller for the engine you target, or keep to the
happy path.

| Behavior | InProc | Durable Task | Temporal | Elsa | Restate | Zeebe |
|---|---|---|---|---|---|---|
| **Duplicate start** (same id twice) | throws while running; once the prior run completed, the id frees and can be re-onboarded | engine-defined (a started id is not re-run) | rejected (`WorkflowAlreadyStarted`) | a second start by the same correlation id | silent no-op (a key runs once ever) | `StartByMessageAsync`: broker dedupes by message id within a TTL. Plain `StartAsync`: no dedup — two starts run two instances sharing one key |
| **Raise before the wait is armed** | buffered | buffered | buffered (durable signal) | rejected (no bookmark yet) | resolved into the promise when the wait arms | broker-correlated (message TTL) |
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
- For single-active start on Zeebe, use `StartByMessageAsync` (TTL-bounded broker dedup); plain `StartAsync`
  has no duplicate-start protection, so start from a `DeterministicInstanceId` and gate re-entry at the seam.

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
