> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Glossary

This page defines the terms of the SoEx.Workflow docs. The terms are in groups by topic. Use this page
to find a term. For a step-by-step introduction, start with the
[tutorials](../tutorials/01-your-first-workflow.md).

## Consumption models

**Consumption model** — A consumption model is one of the two ways to use SoEx.Workflow: the *native
flow* or the *portable flow*. You choose one model for each instance.

**Native flow** — The native flow is the consumption model in which you write the flow in the model of
your runtime. Examples are a Temporal `[Workflow]`, a Durable Task orchestration, an Elsa graph, a Rust
Restate service in a *sidecar*, and a Camunda 8 BPMN diagram. Your step component returns a
*business result*. A *termination hook* for each runtime runs `GovernedTermination`. You get all the
features of each runtime. You write one flow for each runtime. See
[Author a native flow](../how-to/author-a-native-flow.md).

**Portable flow** — The portable flow is the consumption model in which you write one component. Its step
operation returns a *`WorkflowAction`*. The SoEx *driver* for each runtime drives the component. The same
component runs with no change on each runtime. See [Run the portable flow](../how-to/run-the-portable-flow.md).

**Driver** — A driver is the SoEx component that owns the step loop of the portable flow on one runtime
("the \<runtime\> driver"). It sends each step through the *governed core*. It maps the returned
`WorkflowAction` to the durable primitives of the runtime. At completion, it runs the *termination
lifecycle*. SoEx supplies the driver. On InProc, the driver is `WorkflowDriver<I>`.

**`WorkflowAction`** — A `WorkflowAction` is the value that a step of the portable flow returns. It tells
the driver what to do next: `Complete`, `RaiseIntoNext`, `WaitForEvent`, `Delay`, or `Loop`. The
framework puts the typed payloads in an envelope. You pass DTOs.

**`EventBranch`** — An event branch is one way to resume a `WaitForEvent`. It holds an event name and the
step that a raise of that name resumes. A wait has one or more branches. The branches race each other
and the timer of the wait. If more than one event is ready for delivery, the branch declared first wins.

**Event data** — Event data is the data that a raiser sends with an event to a branch that declares its
next step. The `OnEvent` step of the branch runs. The data goes to the step operation as a second
argument, for that one dispatch only. `SealEventData` seals event data. This seal is different from the
seal that supplies a step. See [`WorkflowAction`](workflow-action.md#receiving-data-with-an-event).

**No migration** — A native instance and a portable instance write journals of different shapes. Each
driver can replay only the journal that its own model wrote. To change the model, start a new instance.
There is no in-place upgrade.

## The governed core

**Governed core** — The governed core is the shared `GovernedStep`/`GovernedTermination` machinery that
both models use. Governance is the same in each model: key mint, subject index, idempotency, and
termination lifecycle. Only the method that drives the flow is different.

**Governed step** — A governed step is one governed dispatch of the step component through the SoEx
pipeline. It connects your flow to your component. It applies the governance for each step.
`GovernedStep<I>` implements it.

**`GovernedStep<I>`** — `GovernedStep<I>` sends one dispatch of your step component through the SoEx
pipeline (endpoint pipeline → `DefaultDispatcher` → `component.<op>(typedDto)`). It mints the
*per-instance key*. It adds the *subject* to the index. If you configure an *idempotency store*, it
makes sure that an at-least-once redelivery has one effect only. It returns the typed result of the
component.

**`GovernedTermination`** — `GovernedTermination` runs the *termination lifecycle* at the end of a flow:
`OnRetaining` → destroy the key (*crypto-shred*) → prune the *subject index* → `OnTerminated`, or →
`OnRetentionHeld`.

**Termination hook** — A termination hook is the small part for each runtime that calls
`GovernedTermination` at the end of the flow. Examples are a base orchestrator, a worker interceptor, and
a termination activity or handler.

**Step component** — A step component is your SoEx component that does the work of the steps. It has an
IDesign Method-style contract, for example `IOnboardManager`. Each operation takes one typed step and
returns one result. It does the work of the step in process. The runtime (native flow) or the driver
(portable flow) holds the flow: branches, waits, timers, and the order of the steps.

**Business result** — A business result is the typed value that a step operation of the native flow
returns, for example `StepOutcome`. A step of the portable flow returns a `WorkflowAction`.

**Subsystem entrypoint** — A subsystem entrypoint is the component at the front of a SoEx subsystem.
SoEx.Workflow adds governed durable steps to it.

## Steps, context, and identity

**Workflow instance** — A workflow instance is one run of a flow. Its `InstanceId` identifies it. The
short term is "instance". Governance is for each instance: the key, the subject index entries, and
idempotency.

**`InstanceId`** — The `InstanceId` is the durable identifier of a workflow instance. The context of the
runtime supplies it.

**`Sequence`** — The `Sequence` is the ordinal number of a step in an instance. The context of the
runtime supplies it. With the `InstanceId`, it is part of the *idempotency triple*. A redelivered step
thus applies its effect one time.

**`StepContext`** — A `StepContext` holds the durable `InstanceId`, the `Sequence` of the step, and the
*ambient bytes*. It brings them into `GovernedStep.ExecuteAsync`.

**`StepMetadata`** — `StepMetadata` holds the facts of a step that the framework uses: `InstanceId`,
`Sequence`, `DtoType`, `SubjectIds`, `WorkflowManaged`, and the `IdempotencyKey` triple. The framework
reads them from the envelope. It does not read your payload.

**Ambient / ambient bytes** — The ambient bytes are the serialized ambient context that holds the
`SubjectContext`. You build them one time with `WorkflowEnvelope.AmbientFor`. They go with each
`StepContext`. The framework uses them to index subjects and to route erasure.

**Seed** — The seed is the sealed first step from which an instance starts (`step.SealStep(instanceId, ...)`).
Each step seals the next step with the *per-instance key*. All of the journal thus comes from the seed.
One crypto-shred makes all of it unrecoverable. See
[Author a native flow](../how-to/author-a-native-flow.md).

**Workflow binding / `WorkflowBinding<I>`** — A workflow binding is a usual SoEx binding that hosts your
step component. Put it in your topology and give it to the host when the process starts. It is in the
`SoEx.Transport.Workflow` package, which is the SoEx transport for the workflow seam. Its transport,
channel, endpoint, and `WorkflowListeners` are in the same package.

## Governance, keys, and subjects

**Subject** — A subject is a PII identity that a workflow touches, for example an email address.
`SubjectContext` holds it. You can add subjects during a run. A step that learns a new person declares
that person on the action that it returns (`.Enrolling(...)`). The framework indexes the subject and
puts it in the sealed continuation.

**`SubjectContext`** — `SubjectContext` is the PII subject marker in the ambient bytes. With `Managed`,
the framework indexes the subject and routes erasure for it. With `External`, the system of the consumer
handles the subject.

**Workflow-managed / externally-managed** — These terms tell who handles the erasure of a subject. SoEx
handles a workflow-managed subject (`Managed`). The consumer handles an externally-managed subject
(`External`).

**Per-instance key** — The per-instance key is an AES-256-GCM key for one instance. The framework mints
it on first use and hard-deletes it at termination (*crypto-shred*). The portable flow automatically
seals all that it journals with this key. A native flow seals the data that it persists with this key,
through `SealStep`. In production, the key must be in a durable, shared key store.

**Crypto-shred** — Crypto-shred makes the persisted data of an instance unrecoverable. It hard-deletes
the per-instance key. It does not find and delete the data. Crypto-shred has two conditions. First, the
persisted bytes must be sealed with that key. The portable flow does this for you. A native flow must
seal the data that it persists. Second, the key store must be durable and shared, so that it keeps the
only copy of the key.

**`IInstanceKeyStore`** — `IInstanceKeyStore` is the key store interface. It mints and holds
per-instance keys, encrypts and decrypts with them, and hard-deletes them. `InMemoryInstanceKeyStore` is
an AES-256-GCM implementation for one process only. For production, use a durable store from the
packages:

- `OpenBaoInstanceKeyStore` uses OpenBao Transit. The key stays on the server.
- `RavenDbInstanceKeyStore` uses RavenDB compare-exchange. It holds a data key wrapped by a master key.

Alternatively, implement `IInstanceKeyStore` on your own database, KMS, or HSM.

**`ISubjectIndex` / subject index** — The subject index maps PII subject ids to instance ids for
workflow-managed subjects. Erasure uses it to find each instance that touches a subject. The framework
prunes it at termination. `InMemorySubjectIndex` is supplied.

**`IIdempotencyStore`** — `IIdempotencyStore` is the idempotency store interface. It is optional. It
makes sure that an at-least-once step redelivery has one effect only, keyed on the *idempotency triple*.
`InMemoryIdempotencyStore` is supplied.

**Idempotency triple / `IdempotencyKey`** — The idempotency triple is the `(InstanceId, DtoType, Sequence)`
key. The framework removes duplicate effects of a step on this key.

## Erasure

**Termination** — Termination is the end of a workflow instance: completion, cancellation, or erasure.
`TerminationCoordinator` (below) is a different thing. It drives the termination decisions for erasure.

**Termination lifecycle** — The termination lifecycle is the sequence that runs at termination:

1. Extract the data that you must keep (`OnRetaining`).
2. Crypto-shred the key.
3. Prune the subject index.
4. Run `OnTerminated`. If the extraction fails, run `OnRetentionHeld`.

**Erasure** — Erasure removes the data of a subject. It extracts the data that you must keep. Then it
does a crypto-shred, which makes the other data unrecoverable.

**`IErasureEvent`** — `IErasureEvent` is the interface that a step component hosted in a workflow must
implement. You must choose to implement it. An empty implementation is an explicit choice. Its hooks are
`OnRetaining`, `OnTerminated`, and `OnRetentionHeld`.

**`OnRetaining`** — `OnRetaining` is the extraction hook that runs before the shred. It fires while the
payload is still readable, on each termination path. Write the data that you must keep to a governed
store. It must be idempotent on the idempotency key of the context.

**`OnTerminated`** — `OnTerminated` is the hook that runs after termination and after the shred. Use it
for bookkeeping with no PII, for example an audit or the release of locks.

**`OnRetentionHeld` / retention held / quarantine** — These terms name the state after an extraction
failure. This state is not final. The key stays, the automatic retry stops, and the instance gets a flag
for an audited re-drive. See *Held*.

**Held** — Held is the state of a quarantined instance. Its `OnRetaining` extraction failed after the
retry limit. Its key thus stays, and the framework does not shred it, until an audited re-drive. The
state ("held") and the hook that fires when the instance enters it (`OnRetentionHeld`) are the same
retention obligation. The durable record is in an `IHeldInstanceRegistry`.

**`ErasureCoordinator`** — `ErasureCoordinator` runs a "forget subject S" request from start to end:

1. It stamps the deadline.
2. It finds each instance of the subject in the subject index.
3. For each instance, it decides to let it complete naturally or to force-terminate it.
4. It drives the terminations to crypto-shred or to quarantine.
5. It returns a report.

**`TerminationCoordinator`** — `TerminationCoordinator` drives the termination decisions for erasure. For
one instance, it decides and drives the termination: complete or force-terminate, crypto-shred or
quarantine. `GovernedTermination` is a different thing. It runs the *termination lifecycle* of one
instance at the natural end of a flow.

**`ErasureRequest`** — An erasure request is a request to forget a subject. Its `ReceivedAt` value starts
the statutory clock.

**Statutory deadline / `StatutoryDeadlineClock`** — `StatutoryDeadlineClock` stamps the legal deadline on
an erasure request. If the policy is null, it uses a conservative default window.

**Request-driven re-drive** — A request starts `ErasureCoordinator.EraseAsync`. A "forget subject S"
request re-drives to crypto-shred each instance of that subject that is still indexed and not
terminated. This also closes an instance that was abandoned before its termination hook ran. Two causes
are a hard worker death at the time of termination and an admin terminate or purge.

**Abandoned-instance sweep** — `ErasureCoordinator.SweepAsync(olderThan, resolve)` is the backstop that
needs no request. It lists the live (not shredded) keys through `IEnumerableInstanceKeyStore`. It
force-terminates each instance whose key is older than `olderThan`. An abandoned instance thus gets a
crypto-shred, also when its subject never sends an erasure request. `olderThan` must be longer than the
longest valid flow duration. It is an age limit and does not check if the instance is live.
`ErasureSweepLoop` runs the sweep at an interval. The framework does the shred. The consumer sets the
interval.

## Runtimes and durability

**Runtime / backend** — A runtime is the durable execution engine on which a flow runs: InProc, Durable
Task, Temporal, Elsa, Restate, or Camunda 8 / Zeebe. The docs use the term "runtime". "Backend" occurs
only in code identifiers and fixed names.

**InProc** — InProc is the in-memory runtime (`InMemoryWorkflowRuntime` + `WorkflowDriver<I>`). It has no
durability. A restart loses all of its state. It supports the portable flow only, because it has no
native runtime.

**Durable Task (DTFx / DTS)** — Durable Task is the Durable Task Framework / Durable Task Scheduler. It
gets durability through *event-sourced replay*.

**Temporal** — Temporal is a runtime with event-sourced replay. A native flow on Temporal is a
`[Workflow]` type. The termination runs through the activity of an interceptor, off the replay path.

**Elsa** — Elsa is a runtime with *checkpoint/resume*. It uses bookmarks, for example in SQLite.

**Restate** — Restate is a cross-language runtime with no .NET SDK. The flow runs out of process in a
*sidecar*. The portable flow runs in the framework *Restate sidecar* (`restate-sidecar-rs`). The sidecar
calls back into .NET over HTTP. See the
[Restate adapter README](../../src/SoEx.Workflow.Runtime.Restate/README.md).

**Camunda 8 / Zeebe** — Camunda 8 / Zeebe is a runtime that supports the native flow only. The flow is a
BPMN graph that the broker owns. You draw it in a visual editor. A governed service-task job runs one
`GovernedStep`. A process-end execution-listener job runs the `GovernedTermination` crypto-shred. There is
no portable flow, because BPMN does not express a `WorkflowAction` loop.

**Event-sourced replay** — Event-sourced replay is a durability model. The runtime replays a journal of
events to build the state again (DTFx, Temporal). The flow code must be deterministic. Work that is not
deterministic must stay off the replay path. An example is the change to the key store at termination.

**Checkpoint/resume** — Checkpoint/resume is a durability model. The runtime persists the state at
bookmarks and resumes from them (Elsa).

**Journaled** — Journaled is a durability model. The runtime records each durable step and result in a
journal. The Restate sidecar uses this model when it runs out of process.

**Continue-as-new / `Loop`** — Continue-as-new ends the current execution and starts a new one. Typed
state goes across the boundary. The portable `Loop` action does continue-as-new.

## Packaging and testing

**Adapter** — An adapter is the package for one runtime (`SoEx.Workflow.Runtime.Temporal`,
`.DurableTask`, `.Elsa`, `.Restate`, `.Zeebe`). It connects the *governed core* to that runtime. It
contains a `*WorkflowGateway`, a *driver* (portable flow), a *termination hook* (native flow), or both,
and a `*WorkflowHost` that builds the worker.

**Sidecar** — A sidecar is a Rust binary that runs a Restate flow out of process. It calls back into .NET
over HTTP. Restate supplies no .NET SDK. The framework sidecar is the *Restate sidecar*
(`restate-sidecar-rs`). It serves the portable flow (`OnboardWorkflow`), which calls back into
`RestateWorkflowHost` on `/step` and `/terminate`. It also contains one fixed native onboarding flow
(`NativeOnboardWorkflow`), which the tests use. You write your own native flow as a Restate service in a
sidecar that you build. That flow calls back into your own `/gov-step` and `/gov-terminate` host. The
PiiMaker example has its own sidecar (`examples/PiiMaker/Hosts/Restate/sidecar-rs`).

**Seal** — To seal a step is to serialize it, wrap it in the workflow envelope, and encrypt the result
with the *per-instance key*. To seal is more than to encrypt. `IInstanceKeyStore` does AES-GCM
encryption on bytes. Seal is the full serialize, envelope, and encrypt operation. The *driver* or
`SealStep` does it on the data that goes into the journal.

**Registry / Store / Index** — These are the name suffixes for durable governance state:

- A **Store** holds keyed lifecycle state on the execution path (`IInstanceKeyStore`, `IIdempotencyStore`).
- An **Index** is a subject↔instance lookup (`ISubjectIndex`).
- A **Registry** is a set of open obligations for maintenance (`IHeldInstanceRegistry`,
  `IErasureRequestRegistry`).

**Tier-1 / Tier-2** — These are the test tiers. *Tier-1* is the hermetic set that a plain run executes.
It runs with no external runtime: InProc, the Temporal time-skipping environment, and Elsa with its
in-memory provider. *Tier-2* is the set of deployment-shaped tests. They are opt-in, and you select them
by category. The category `Tier2Hermetic` selects the Tier-2 tests that need no infrastructure. These
tests use SQLite files, for example Elsa on SQLite. The other Tier-2 tests need a real runtime or store:
Temporal, DTS, Restate, Camunda 8 / Zeebe, OpenBao, or RavenDB. A selected test fails when its runtime
is unreachable. See *Verifying locally* in the runtime matrix.
