> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Reference — runtime matrix

This page shows how the SoEx model maps to each runtime. It also shows the trigger behavior of each
runtime. The governed core is the same on each runtime. It has one pipeline, one key mint, one subject
index, one idempotency model, and one termination lifecycle. The flow and the edge behaviors are
different on each runtime. For the reasons, see
[Runtimes and durability](../explanation/runtimes-and-durability.md).

## Guards on visible values

The framework guards the values that a runtime can see. A guard keeps a known subject out of the value.
The values that the framework guards depend on the consumption model.

- **Portable flow.** The portable flow controls each value that the runtime can see. It guards all of
  them automatically: the instance id, each step result, the names of its waits and timers, and its final
  return value.
- **Native flow.** You write the flow. The framework guards the values that go through `GovernedStep`:
  the instance id and each step result, on each runtime. You are responsible for the return value of the
  orchestration and for each wait name or event name that you choose. Keep PII out of these values, or
  pass them through `IGovernedStep.GuardVisibleName(...)`. The drivers use the same method. The Zeebe host
  applies it to its job names and incident names.

Keep PII out of the exception messages of your steps. Before a failure message goes into the durable
state of the runtime, the framework removes each known subject from it. This removal is a substring
safety net. It does not detect all PII.

Camunda 8 / Zeebe has a deploy-time check for your part of this work. A BPMN flow is a declarative
artifact. At deploy time, `ZeebeWorkflowHost.ValidateResource` checks the io-mappings of the diagram. It
gives a warning if a service task copies a variable of the framework (`seed`/`instanceId`) into a
journaled variable that has an unguarded name.

The other native runtimes have no deploy-time check. On these runtimes, you write the flow in imperative
code. That code has no declarative surface to scan. It also has no replay-deterministic point at which
the framework can guard the return value of the flow. The `GovernedStep` guards apply on each runtime:
the instance id and each step result. You are responsible for the other values. If you move a flow from
Zeebe to another runtime, you lose the deploy-time warning. You keep the `GovernedStep` guards. See the
*Native PII-guard tooling* row below.

## How the model maps (native flow)

A native flow has no `WorkflowAction`. The `.Enrolling(...)` method of the portable flow thus does not
apply. The author of the flow owns the `StepContext`, and thus owns the ambient bytes.

In the portable flow, enrollment of a subject that a step learned works the same on all five runtimes
that support the portable flow. Each driver adds the declared subjects before it flattens the action.
The subject goes in the sealed continuation and never goes into the journal. The cross-runtime
conformance suite covers this behavior, so the table below has no row for it.

| Concept | DTFx | Temporal | Elsa | Restate | Camunda 8 / Zeebe |
|---|---|---|---|---|---|
| **Flow (you write it)** | `GovernedTaskOrchestrator.Flow` (CallActivity + WaitForExternalEvent) | `[Workflow]` (ExecuteActivity + WaitConditionAsync) | registered Elsa workflow (activities + bookmarks) | a Rust Restate service in your own sidecar (`ctx.run` + durable promise) | BPMN diagram (service tasks + message-catch events). The broker owns it. |
| **Governed step** | step activity → `GovernedStep.ExecuteAsync` | `[Activity]` → same | activity → same | `POST /gov-step` → same | service-task job worker → same |
| **Step dispatch** | `WorkflowEndpoint<I>` → `EndpointPipeline.ServicePipeLine<I>` → `DefaultDispatcher` → `component.<op>(typedDto)` | ← same | ← same | ← same (over HTTP) | ← same (through the job worker) |
| **Termination hook** | base orchestrator → `GovernedTerminationActivity` | `GovernedTerminationInterceptor` → termination activity | `GovernedTerminationActivity` | `POST /gov-terminate` → `GovernedTermination` | process-end execution-listener job → `GovernedTermination` |
| **Durability model** | event-sourced replay | event-sourced replay | checkpoint/resume (bookmarks) | journaled (sidecar out of process) | journaled by the broker (process variables) |
| **Native PII-guard tooling** | none. You guard the values. The `GovernedStep` guards apply. | none. Same as DTFx. | none. Same as DTFx. | none. Same as DTFx. | deploy-time lint of BPMN io-mappings (`ValidateResource`) |
| **Subject learned in the flow** | You make a new `SubjectContext.Managed(...)` ambient and pass it on the `StepContext`. | ← same | ← same | ← same | ← same |

The *Step dispatch* row is the same on each runtime. The SoEx endpoint pipeline calls the component in
the same way on each runtime. Your step code is thus the same on each runtime. Only the flow around it
changes.

On Restate, a native flow is Rust code. You write it as a Restate service in a sidecar that you build. The
PiiMaker example does this in `examples/PiiMaker/Hosts/Restate/sidecar-rs`. The framework sidecar
(`restate-sidecar-rs`) serves the portable flow (`OnboardWorkflow`). It also contains one fixed native
onboarding flow (`NativeOnboardWorkflow`), which the Tier-2 tests use.

## Availability

| Runtime | Native flow | Portable flow |
|---|---|---|
| InProc | — (InProc has no native runtime) | Yes (always portable) |
| Durable Task | Yes | Yes |
| Temporal | Yes | Yes |
| Elsa | Yes | Yes (durable timers need a resumer that you host. See below.) |
| Restate | Yes | Yes |
| Camunda 8 / Zeebe | Yes | — (native flow only) |

> **On Elsa, portable durable timers need a resumer that you host.** On the other runtimes, a portable
> `WorkflowAction.Delay` or a wait with a timeout fires on the clock of the runtime. On Elsa, the driver
> suspends the instance on a `__timer` bookmark. The bookmark holds its due time (`dueAt`) and the sealed
> step to resume. The framework does not host a scheduler for Elsa. In an Elsa deployment with no added
> scheduler, no component fires the timer. A production Elsa host must run a background resumer. The
> resumer scans the due `__timer` bookmarks and resumes them. Alternatively, configure the scheduling
> feature of Elsa. The bookmark records all the data that a resumer needs. You must host the resumer.
> Until you do, a portable timer on Elsa stays parked for an indefinite time. For a portable flow that
> uses durable timers, we recommend Temporal.

## Gateway semantics

The `IWorkflowGateway` interface is the same on each runtime. The normal path behaves the same on each
runtime. A shared gateway conformance suite in the private test repo checks this. It asserts the same
start→raise behavior on all adapters, and it includes the rejection of a duplicate start.

Each adapter that can detect a duplicate start of a live id raises
`WorkflowInstanceAlreadyExistsException`. A caller thus catches one type on each runtime. The example
shows this exception as an HTTP 409. One edge behavior is different between runtimes. One runtime cannot
detect a duplicate start. Design your caller for the runtime that you use.

| Behavior | InProc | Durable Task | Temporal | Elsa | Restate | Zeebe |
|---|---|---|---|---|---|---|
| **Duplicate start** (same id two times) | `WorkflowInstanceAlreadyExistsException` while the instance runs. A completed id becomes free and you can onboard it again. | `WorkflowInstanceAlreadyExistsException` while the instance is live. A completed id becomes free. | `WorkflowInstanceAlreadyExistsException` (from `WorkflowAlreadyStarted`) | `WorkflowInstanceAlreadyExistsException` (a run already holds the correlation) | `WorkflowInstanceAlreadyExistsException`. A key runs one time only, so a completed key stays taken. | plain `StartAsync`: **the adapter cannot detect it**. The broker makes its own key, so the caller is responsible for start idempotency. `StartByMessageAsync` removes duplicates by message id within a TTL. |
| **Raise before the wait is armed** | buffered | buffered | buffered (durable signal) | rejected (no bookmark yet) | goes into the promise when the wait arms | correlated by the broker (message TTL) |
| **Multi-branch wait** (named events that race the timer) | All branches park at the same time. Declared order breaks a tie. | one external-event receiver for each branch | one signal name for each branch, checked in declared order | one bookmark for each branch. On resume, the other bookmarks are burned. | one durable promise for each branch, all raced together. Write-once for each name in each generation. | portable flow not available (native BPMN only) |
| **Statechart-backed step** ([how-to](../how-to/drive-a-flow-with-a-statechart.md)) | yes | yes | yes | yes. A machine with an `after(...)` timer needs the timer resumer above. | yes. Exception: a machine that the SAME event name can resume two times in one generation (write-once promise). | not available (no portable flow) |
| **Idempotent raise** (`raiseId`) | removes duplicates (set of handled ids for each instance, for the life of the instance) | removes duplicates (portable flow, for each generation. The set resets at continue-as-new.) | removes duplicates (portable flow, for each generation. The set resets at continue-as-new.) | removes duplicates if you configure an `IIdempotencyStore`. Otherwise, `NotSupportedException`. | removes duplicates by design (write-once promise. `raiseId` is advisory.) | removes duplicates by broker message id within the TTL |

Results for the caller:

- On InProc, you can onboard a completed id again as a new generation. On Restate, a key runs one time
  only. Use the rule of the runtime that you target.
- On Elsa, make sure that the wait is armed before you raise, or retry the raise. If you need idempotent
  raises, configure an `IIdempotencyStore`. Elsa is also the one adapter that resolves the raise in the
  host. The other adapters resolve it in the flow. The cause is that Elsa drives workflow definitions
  that you write.
- A raise of an event that the instance already handled runs its `OnEvent` continuation again, under a
  new sequence. The framework does not remove duplicates by event name. Two raises of one name are two
  business events. To make one raise idempotent, give it a `raiseId`.
- On Temporal and Durable Task, the `raiseId` set is for one generation. It resets at continue-as-new.
  A retried raise that crosses a `Loop` (CAN) boundary can thus arrive two times. If a raise must occur
  exactly one time across a CAN boundary, control it with a durable effect. The in-memory set is not
  sufficient for this.
- At the CAN boundary, Temporal and Durable Task behave in opposite directions. Durable Task does
  continue-as-new with `preserveUnprocessedEvents: false`. A raise that arrives in the continue-as-new
  transition window is **dropped**. It does not go into the next generation. This behavior is
  intentional. If buffered events went forward, the removal of duplicates for each generation would
  reset. If a raise near a `Loop` must not be lost, make it re-drivable. Raise it again until the flow
  acknowledges it.
- For a single active start on Zeebe, use `StartByMessageAsync`. The broker removes duplicates for the
  TTL. Plain `StartAsync` has no protection against a duplicate start. With plain `StartAsync`, start
  from a `DeterministicInstanceId` and control re-entry at the seam.
- Elsa has the same hazard on a plain start by correlation id. Two live instances of one logical id share
  one key. The first instance to terminate shreds the live data of the other. Control the single active
  start on Elsa too.
- A multi-branch wait behaves the same on each runtime that has the portable flow, with one exception.
  On Restate, a durable promise is write-once for each event NAME for the life of a generation. A branch
  that can get more than one raise thus delivers only its first raise. An example is a resend button. To
  get the next raise, the flow must take a `Loop` after it handles the first one. The `Loop` starts a new
  generation with new promises. The other runtimes consume the delivery and arm the branch again, so a
  repeated raise at one branch works.
- On Elsa, a raise can arrive for a branch after another branch has resumed the wait. Elsa rejects this
  raise and does not buffer it, because resume burns the bookmarks. This is the same as the "raise
  before the wait is armed" row above. The rejection is visible to the caller.
- The Zeebe raise TTL is the time for which the broker buffers a message. If the message does not
  correlate in that time, the broker drops it with no error. The default is 5 minutes. You can set it on
  the gateway (`raiseTtl`). For a flow that arms its wait slowly, set the TTL to the worst-case time to
  arm the wait.

## In-flight evolution

This section tells what occurs to in-flight instances when you deploy a changed flow. Each row gives the
result for one runtime. For the full reasons and the pin-and-drain pattern, which needs no store, see
[Versioning and evolution](../explanation/versioning-and-evolution.md).

| Runtime | Portable flow | Native flow | Tool for a breaking change |
|---|---|---|---|
| InProc | No durability across a restart, so in-flight instances do not continue after a deploy. | — | not applicable |
| Durable Task | safe (the step is an activity). In-flight instances roll forward. | The orchestrator is replayed. There is no in-code patch API. | Give the new version a new orchestration name. Drain the old version. |
| Temporal | safe (the step is an activity). In-flight instances roll forward. | The `[Workflow]` is replayed. A control-flow change can throw a non-determinism error. | `Workflow.Patched` / `GetVersion`, or Worker Build-ID versioning |
| Elsa | Elsa versions definitions natively. In-flight instances stay pinned. | ← same | Publish a new definition version. Pin a specific version to hold new starts back. |
| Restate | Restate versions deployments natively. In-flight invocations stay pinned. | ← same | Deploy a new sidecar deployment. |
| Camunda 8 / Zeebe | — (native flow only) | Zeebe versions BPMN definitions natively. In-flight instances stay pinned. | new BPMN version, or Camunda process-instance migration |

The portable flow rolls forward on each runtime that runs it. Your step code is off the replay path. A
backward-compatible change thus needs only a new deploy. If old instances must never run new code, give
the new version its own instance-id space. Put a version token in the `DeterministicInstanceId` prefix.
Then drain the old instances. For the procedure, see
[Evolve a running flow](../how-to/evolve-a-running-flow.md).

## Step failure, retry, and poison

This section tells what occurs when a governed step throws an exception. The framework applies one
default on all runtimes: a bounded retry, then **park-before-shred**. Failure behavior is thus the same on
each runtime. A transient failure cannot destroy the sealed journal.

`WorkflowStepOptions` sets the retry. It holds the maximum number of attempts, the backoff, the timeout
for each step, and the predicate for a terminal exception. Each adapter maps these options to the retry
mechanism of its runtime. All drivers use one park path (`GovernedTermination.QuarantineAsync`).

The failure path and the erasure path are different paths:

1. A step that fails gets a **retry**, up to the bound.
2. When the attempts are used, or when the failure is terminal, the framework **parks** the instance.
3. A parked instance keeps its key. The framework records the key in the held registry and fires
   `OnRetentionHeld`. It does *not* crypto-shred the instance.
4. To recover a parked instance, do an audited re-drive (resume). Alternatively, terminate it
   intentionally. The termination then shreds it.

The framework destroys the key only on an intentional termination. An intentional termination is a
natural completion, or an erasure or force-terminate through the coordinator. The failure path never
destroys the key.

| Behavior | InProc | Durable Task | Temporal | Elsa | Restate | Zeebe |
|---|---|---|---|---|---|---|
| **Retry** | driver loop (bounded exponential backoff) | activity `RetryPolicy` (`TaskOptions`) | activity `RetryPolicy` | driver loop | sidecar retry (see the caution below) | broker job retries, one less for each failure |
| **When the retries are used** | park (key kept, held) | park (quarantine activity) | park (quarantine activity) | park (key kept, held) | incident (see the caution below) | broker incident (key kept) |
| **Default** | 3 attempts, 1s → 2s backoff | ← same | ← same | ← same | see the caution below | BPMN task `retries`, then incident |
| **Settings for each binding** | `WorkflowStepOptions` on the driver | static default in the orchestration (the SDK constructs it) | ← same | `WorkflowStepOptions` on the activity | sidecar configuration | BPMN `retries` attribute |

Earlier versions had destructive defaults. These defaults are removed:

- Durable Task and Elsa did a crypto-shred of the instance key on the **first** failure. A transient
  database fault thus erased the flow permanently.
- Temporal had no retry policy. The server default thus retried a step that failed **forever, with no
  signal**.

Durable Task, Elsa, and Temporal now do a bounded retry, then park.

> [!CAUTION]
> Configure external alerts for stuck instances on Restate. The Rust sidecar retries a step that fails with
> infinite backoff. The Restate runtime retries an HTTP 500. The sidecar does not yet drive the park
> path. A poison step on Restate thus retries for an indefinite time and does not park. A bound on the
> sidecar retry and park-before-shred on the sidecar are a tracked follow-up.

> **Options for each binding on the replay runtimes.** On Durable Task and Temporal, the SDK constructs
> the orchestration or workflow on the replay path. It thus reads the retry policy from a static default.
> It does not read a `WorkflowStepOptions` for each binding. Tune the default in one central place.
> Overrides for each binding on these two runtimes are a follow-up. The InProc and Elsa drivers take
> options for each binding directly.

## Verifying locally

The tests run each runtime against the real runtime. Full verification thus needs each runtime to be
available. A test run always reports its true coverage. The suite has two sets:

- **The hermetic set.** A plain run executes this set. It uses InProc, the in-memory stores, the
  time-skipping environment of Temporal, and Elsa with its in-memory provider. It needs no
  infrastructure. On a bare machine, it passes with no skipped cases.
- **The hermetic Tier-2 set.** These tests use SQLite files, for example Elsa on SQLite, or the Temporal
  time-skipping server. They need no infrastructure. They are opt-in. You select them with the category
  `Tier2Hermetic`.
- **The runtime-bound set.** Each test that needs a real runtime is opt-in. You select these tests by
  category. The filter of a run thus states which runtimes the run covers.

A selected test that cannot reach its runtime fails. It does not skip. Each test is thus in one of two
states. Either the run did not select it, and the test is absent from the results. Or the run selected
it, and the test passed or failed against a live runtime. A failed build of the Restate sidecar is also
a hard failure. A sidecar that is present but broken certifies nothing.

Before you select the runtime-bound tests, start the runtimes that they use:

- Temporal, on port 7233.
- The Durable Task Scheduler (DTS) emulator, on port 8080.
- restate-server, on ports 8088 (ingress) and 9070 (admin). The Restate tests also need `cargo` to build
  the sidecar.
- Camunda 8 / Zeebe, on ports 26500 (gRPC) and 8090 (REST).
- OpenBao, on port 8200, for the key-store tests.

The RavenDB tests use an embedded RavenDB server. They need the RavenDB.Embedded server binaries on the
machine. `examples/dev/piimaker.sh provision-only` starts these runtimes in Docker.

For the full setup and for the timing traps that can give false results, see
[Verify it yourself](../how-to/verify-it-yourself.md).

## See also

- [Triggering reference](triggering.md) — the gateway, the sealer, and the id types.
- [Author a native flow](../how-to/author-a-native-flow.md) — the procedure for each runtime.
- [Versioning and evolution](../explanation/versioning-and-evolution.md) — the reasons for the
  in-flight evolution summary above.
