> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Glossary

The terms used across the SoEx.Workflow docs, grouped by theme. For the narrative, start with the
[tutorials](../tutorials/01-your-first-workflow.md); this page is the lookup.

## Consumption models

**Consumption model** — One of the two ways to consume SoEx.Workflow: the *native flow* or the
*portable flow*. You pick exactly one per instance.

**Native flow** — The model where you author the flow in your runtime's own model (a Temporal
`[Workflow]`, a Durable Task orchestration, an Elsa graph, the Restate sidecar (`restate-sidecar-rs`), a
Camunda 8 BPMN diagram). Your step component returns a *business result*, and a per-backend *termination hook* runs
`GovernedTermination`. Full backend expressiveness, at the cost of a flow per backend. See
[Author a native flow](../how-to/author-a-native-flow.md).

**Portable flow** — The model where you write one component whose step operation returns a
*`WorkflowAction`*, and SoEx's generic per-backend *driver* drives it. The same component runs unchanged
on every runtime. See [Run the portable flow](../how-to/run-the-portable-flow.md).

**Driver** — In the portable model, SoEx's generic per-backend driver that owns the step loop ("the
\<runtime\> driver"): it dispatches each step through the *governed core*, routes the returned
`WorkflowAction` onto the backend's durable primitives, and runs the *termination lifecycle* on
completion. You don't write it; on InProc it is `WorkflowDriver<I>`.

**`WorkflowAction`** — The value a portable-model step returns, telling the driver what to do next:
`Complete`, `RaiseIntoNext`, `WaitForEvent`, `Delay`, or `Loop`. The framework envelopes the typed
payloads, so you pass DTOs, not bytes.

**No migration** — A native instance and a portable instance have different durable journal/replay
shapes, so neither driver can replay the other's history. To switch models you start a fresh instance;
there is no in-place upgrade.

## The governed core

**Governed core** — The shared `GovernedStep`/`GovernedTermination` machinery both models build on.
Governance (key mint, subject index, idempotency, termination lifecycle) is identical regardless of model;
only how the flow is driven differs.

**Governed step** — The seam between your flow and your component: one governed dispatch of the step
component through the SoEx pipeline, with per-step governance applied. Realized by
`GovernedStep<I>`.

**`GovernedStep<I>`** — Wraps one dispatch of your step component through the SoEx pipeline
(endpoint pipeline → `DefaultDispatcher` → `component.<op>(typedDto)`), minting the *per-instance key*,
indexing the *subject*, and (when an *idempotency store* is wired) collapsing at-least-once
redelivery to a single effect. Returns the component's typed result.

**`GovernedTermination`** — Runs the *termination lifecycle* at the end of a flow: `OnRetaining` → destroy the
key (*crypto-shred*) → prune the *subject index* → `OnTerminated`, or → `OnRetentionHeld`.

**Termination hook** — The small per-backend piece that calls `GovernedTermination` at the end of the flow (a
base orchestrator, a worker interceptor, a termination activity/handler).

**Step component** — Your plain SoEx component (an IDesign Method-style contract such as `IOnboardSteps`): one
typed step in, one result out. It does the step's work in-process and holds no flow — branches, waits,
timers, and sequencing live in the backend (native) or in the driver (portable).

**Business result** — The typed value a native-model step operation returns (e.g. `StepOutcome`). A
portable-model step returns a `WorkflowAction` instead.

**Subsystem entrypoint** — The component that fronts a SoEx subsystem — the thing you add governed
durable-step execution to.

## Steps, context, and identity

**Workflow instance** — One running occurrence of a flow, identified by its `InstanceId`. Governance
(key, subject index entries, idempotency) is scoped per instance.

**`InstanceId`** — The durable identifier of a workflow instance, supplied by the backend's own context.

**`Sequence`** — The per-step ordinal within an instance, from the backend's context. With `InstanceId`
it keys the *idempotency triple*, so a redelivered step applies its effect once.

**`StepContext`** — Carries the durable `InstanceId`, the per-step `Sequence`, and the flowed *ambient
bytes* into `GovernedStep.ExecuteAsync`.

**`StepMetadata`** — The framework-understood facts of a step (`InstanceId`, `Sequence`, `DtoType`,
`SubjectIds`, `WorkflowManaged`, and the `IdempotencyKey` triple), extracted from the envelope without
interpreting your payload.

**Ambient / ambient bytes** — The serialized ambient context (built once with
`WorkflowEnvelope.AmbientFor`) that carries the `SubjectContext`, flowed on each `StepContext` so the
framework can index subjects and route erasure.

**Seed** — The sealed first step an instance starts from (`step.SealStep(instanceId, ...)`). Each step
seals the next under the *per-instance key*, so the whole journal traces back to the seed and
crypto-shred renders all of it unrecoverable at once. See
[Author a native flow](../how-to/author-a-native-flow.md).

**Workflow binding / `WorkflowBinding<I>`** — An ordinary SoEx binding that hosts your step component;
you put it in your topology and feed it to the host at process startup. It lives in the
`SoEx.Transport.Workflow` package (the SoEx transport for the workflow seam), alongside its transport,
channel, endpoint, and `WorkflowListeners`.

## Governance, keys, and subjects

**Subject** — A PII identity (e.g. an email) touched by a workflow, carried in `SubjectContext`.
Subjects are additive — a later step may name more.

**`SubjectContext`** — The PII subject marker attached to ambient bytes. `Managed` means the framework
indexes and routes erasure for the subject; `External` defers subject handling to the consumer's own
system.

**Workflow-managed / externally-managed** — Whether a subject's erasure is handled by SoEx (`Managed`)
or left to the consumer (`External`).

**Per-instance key** — An AES-256-GCM key minted on first use and hard-deleted at termination
(*crypto-shred*). The portable flow seals everything it journals under it automatically; a native flow
seals what it persists with it (via `SealStep`). Must live in a durable, shared key store in production.

**Crypto-shred** — Rendering an instance's persisted data unrecoverable by hard-deleting its
per-instance key, rather than locating and deleting the data itself. Only holds if the persisted bytes
were sealed under that key (the portable flow does this for you; a native flow must seal what it
persists) and the key store survives long enough to be the only copy (durable, shared).

**`IInstanceKeyStore`** — Mints, holds, encrypts/decrypts with, and hard-deletes per-instance keys.
`InMemoryInstanceKeyStore` is an AES-256-GCM implementation, in-process only. For production use a bundled
durable store — `OpenBaoInstanceKeyStore` (OpenBao Transit; the key never leaves the server) or
`RavenDbInstanceKeyStore` (RavenDB compare-exchange holding a master-key-wrapped data key) — or implement
`IInstanceKeyStore` against your own DB/KMS/HSM.

**`ISubjectIndex` / subject index** — Maps PII subject ids to instance ids for workflow-managed
subjects, so erasure can find every instance touching a subject. Pruned at termination.
`InMemorySubjectIndex` is provided.

**`IIdempotencyStore`** — Optional store that collapses at-least-once step redelivery to a single effect
on the *idempotency triple*. `InMemoryIdempotencyStore` is provided.

**Idempotency triple / `IdempotencyKey`** — The `(InstanceId, DtoType, Sequence)` key on which a step's
effect is deduplicated.

## Erasure

**Termination** — The end of a workflow instance: completion, cancellation, or erasure. Distinct from
`TerminationCoordinator` (below), which is the erasure-side decision driver.

**Termination lifecycle** — What runs at termination: extract must-retain data (`OnRetaining`), crypto-shred
the key, prune the subject index, then `OnTerminated` (or `OnRetentionHeld` on extraction failure).

**Erasure** — Forgetting a subject's data: extract any must-retain data, then crypto-shred so the rest
is unrecoverable.

**`IErasureEvents`** — The interface a workflow-hosted step component must implement (a deliberate
opt-in; a no-op is an explicit choice). Its hooks are `OnRetaining`, `OnTerminated`, `OnRetentionHeld`.

**`OnRetaining`** — Pre-shred extract; fires while the payload is still readable, on every termination path.
Write must-retain data to a governed store. Must be idempotent on the context's idempotency key.

**`OnTerminated`** — Post-termination, post-shred, PII-free bookkeeping (audit, release locks).

**`OnRetentionHeld` / retention held / quarantine** — Extraction-failure state (non-final): the key
is retained, auto-retry stopped, and the instance flagged for an audited re-drive. See *Held*.

**Held** — The state an instance is in while quarantined: its `OnRetaining` extraction failed past the
retry boundary, so its key is retained (not shredded) pending an audited re-drive. The state ("held")
and the hook that fires on entry (`OnRetentionHeld`) describe the same retention obligation; the
durable record lives in an `IHeldInstanceRegistry`.

**`ErasureCoordinator`** — Runs a "forget subject S" request end to end: stamps the deadline, fans out
across the subject index, decides per instance whether to complete naturally or force-terminate, drives
terminations to crypto-shred (or quarantine), and returns a report.

**`TerminationCoordinator`** — The erasure-side decision driver: for one instance it decides and drives
the termination (complete vs force-terminate, crypto-shred vs quarantine). Distinct from
`GovernedTermination`, which runs the per-instance *termination lifecycle* at a flow's natural end.

**`ErasureRequest`** — A request to forget a subject; its `ReceivedAt` anchors the statutory clock.

**Statutory deadline / `StatutoryDeadlineClock`** — Stamps the legal deadline for an erasure request (a
null policy → a conservative default window).

**Request-driven re-drive** — `ErasureCoordinator.EraseAsync` is request-triggered: a "forget subject S"
request re-drives any still-indexed, un-terminated instance for that subject to crypto-shred, which also
closes out an instance abandoned before its per-backend termination hook ran (hard worker death at the
termination instant, admin terminate/purge).

**Abandoned-instance sweep** — `ErasureCoordinator.SweepAsync(olderThan, resolve)` is the
request-independent backstop: it enumerates the live (un-shredded) key set via
`IEnumerableInstanceKeyStore` and force-terminates every instance whose key was minted longer than
`olderThan` ago, so an abandoned instance whose subject never files an erasure request is still
crypto-shredded. `olderThan` must exceed the longest legitimate flow duration (it is an age threshold;
it does not probe liveness). `ErasureSweepLoop` runs it on an interval; the framework performs the
shred, and the consumer chooses the cadence.

## Runtimes and durability

**Runtime / backend** — The durable-execution engine a flow runs on: InProc, Durable Task, Temporal,
Elsa, Restate, or Camunda 8 / Zeebe.

**InProc** — The in-memory runtime (`InMemoryWorkflowRuntime` + `WorkflowDriver<I>`); no durability,
nothing survives a restart. Always portable (it has no native backend).

**Durable Task (DTFx / DTS)** — Durable Task Framework / Durable Task Scheduler; durability by
*event-sourced replay*.

**Temporal** — Event-sourced replay runtime; native flows are `[Workflow]` types and the termination runs
via an interceptor's activity, off the replay path.

**Elsa** — *Checkpoint/resume* runtime (bookmarks, e.g. SQLite).

**Restate** — Cross-language runtime with no .NET SDK; the flow runs out-of-process in the *Restate
sidecar* (`restate-sidecar-rs`), which calls back into .NET over HTTP. See the
[Restate adapter README](../../src/SoEx.Workflow.Runtime.Restate/README.md).

**Camunda 8 / Zeebe** — *Native-only* runtime: the flow is a BPMN graph the broker owns, authored in a
visual editor. A governed service-task job runs one `GovernedStep`; a process end execution-listener job
runs the `GovernedTermination` crypto-shred. No portable flow (a `WorkflowAction` loop is not expressed on BPMN).

**Event-sourced replay** — Durability by replaying a journal of events to rebuild state (DTFx,
Temporal). Flow code must be deterministic; non-deterministic work — like the key-store mutation at
termination — must stay off the replay path.

**Checkpoint/resume** — Durability by persisting state at bookmarks and resuming from them (Elsa).

**Journalled** — Durability by recording each durable step/result in a journal (the Restate sidecar's
out-of-process run).

**Continue-as-new / `Loop`** — Ending the current execution and starting a fresh one carrying typed
state across the boundary (the portable `Loop` action).

## Packaging and testing

**Adapter** — A per-runtime package (`SoEx.Workflow.Runtime.Temporal`, `.DurableTask`, `.Elsa`, `.Restate`,
`.Zeebe`) that wires the *governed core* onto one backend: a `*WorkflowGateway`, a *driver* (portable)
and/or a *termination hook* (native), and a `*WorkflowHost` to build the worker.

**Sidecar** — The *Restate sidecar* (`restate-sidecar-rs`): the out-of-process Rust binary that runs the
Restate orchestration and calls back into the .NET `RestateWorkflowHost` over HTTP, because Restate ships
no .NET SDK. One binary serves both the portable and native Restate flows.

**Seal** — Serialize a step, wrap it in the workflow envelope, and encrypt the result under the
*per-instance key*. Broader than raw *encrypt* (`IInstanceKeyStore` does AES-GCM on bytes): sealing is
the whole serialize-envelope-encrypt operation the *driver* / `SealStep` performs on what gets journaled.

**Registry / Store / Index** — The suffix rule for durable governance state: a **Store** holds keyed
lifecycle state on the execution path (`IInstanceKeyStore`, `IIdempotencyStore`); an **Index** is a
subject↔instance lookup (`ISubjectIndex`); a **Registry** is a maintenance-side set of outstanding
obligations (`IHeldInstanceRegistry`, `IErasureRequestRegistry`).

**Tier-1 / Tier-2** — The test tiers. *Tier-1* is the hermetic set, running with no external backend
(InProc, plus time-skipping or in-memory adapter fixtures); *Tier-2* exercises the durable backends
(Temporal, DTS, Restate, Elsa SQLite), is selected by category, and fails when a selected backend is
unreachable. See the runtime matrix's *Verifying locally*.
