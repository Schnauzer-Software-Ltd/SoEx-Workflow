> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Runtimes and durability

SoEx.Workflow runs on six runtimes. Five are durable production runtimes: Durable Task, Temporal, Elsa,
Restate, and Camunda 8 / Zeebe. The sixth, InProc, keeps no state across a restart. Use InProc for tests
and demos. Each runtime has its own durability mechanism. This page describes the durability models, how
the SoEx governance maps onto each runtime, and why some behaviors are different on different runtimes.
The [runtime matrix](../reference/runtime-matrix.md) gives the tables.

## The governed step is the same on each runtime

On each runtime, the SoEx endpoint pipeline calls your component for each governed step. The same
per-step governance applies: the key, the subject index, and idempotency. The same termination lifecycle
applies. Your step code is the same on each runtime. Two things change from runtime to runtime: the flow
around the steps, and the durability mechanism below them.

## Four durability models

Durable execution means that an instance survives the death of its process and then resumes. The
runtimes use four models to do this.

**Event-sourced replay** (Durable Task, Temporal). The runtime rebuilds the state when it replays a
journal of events. The runtime runs the flow code again on replay, so the flow code must be
deterministic. Each non-deterministic operation must run off the replay path, inside an activity.
Examples are wall-clock reads, random ids, and the most important one: the key-store mutation at the
termination. For this reason, the native-flow termination on these runtimes always runs in an activity,
never inline in the workflow. On Durable Task, a base orchestrator schedules the termination activity
from a `finally` block. On Temporal, a worker interceptor schedules it.

**Checkpoint/resume** (Elsa). The runtime persists the state at bookmarks and resumes from them. The flow
parks on a bookmark, and an event resumes it. Elsa does not replay, so determinism is less of a
constraint. A correlation drives the resume, and the flow code does not run again.

**Journalled, out-of-process** (Restate). The flow runs in a separate process, a Rust sidecar. The
sidecar journals each durable step and calls back into .NET over HTTP. All of the governance is on the
.NET side. Restate sees only ciphertext.

**Broker-journalled** (Camunda 8 / Zeebe). The broker owns the flow as a BPMN graph and journals the
process variables. The .NET side contains only job workers and a termination listener.

## How the model maps

Each runtime has the same three parts: a flow, governed steps, and a termination hook. Each runtime
implements these parts with its own primitives:

| | Flow is… | A step is… | The termination is… |
|---|---|---|---|
| Durable Task | an orchestration | a `CallActivity` → governed step | a base-orchestrator `finally` → termination activity. A continue-as-new skips it. |
| Temporal | a `[Workflow]` | an `[Activity]` → governed step | a worker interceptor that schedules the termination activity on completion, failure, or cancel. A continue-as-new skips it. |
| Elsa | a registered graph | an activity → governed step | `GovernedTerminationActivity` as the last step of the graph. It runs one time, as a normal step. |
| Restate | a Rust Restate service in your sidecar | a `ctx.run` → `/gov-step` callback | a final `ctx.run` → `/gov-terminate` callback |
| Zeebe | a BPMN diagram | a service-task job → governed step | a process end execution-listener job |
| InProc | the portable flow | a driver-driven dispatch | the driver's completion path |

In the portable flow, one generic driver is the flow on each runtime. In a native flow, you write each
flow in the idiom of its runtime. InProc runs the same governed step and the same termination. It holds
state in memory only, and it loses all state on a restart. InProc is thus **not** one of the four
durability models above. Use it for tests and demos, and do not use it in production.

## Trigger semantics on each runtime

Start and raise operations behave differently on different runtimes. Each runtime has its own model of
identity and of signals.

A duplicate start has a different result on each runtime:

- Restate keys a workflow by a value that runs one time only. A second start raises
  `WorkflowInstanceAlreadyExistsException`, also after the first run completes.
- Temporal rejects an id that already started.
- InProc frees the id of a completed instance. You can then onboard that id again as a new generation.

Each of these behaviors is correct for its identity model.

A raise can arrive before its wait is armed. The result depends on the runtime:

- Temporal and Durable Task have durable signals. They buffer the raise.
- Restate resolves the raise into a promise.
- Elsa rejects the raise. A resume needs a bookmark, and the bookmark does not exist yet.

Each runtime deduplicates idempotent raises with its own mechanism:

- InProc, Durable Task, and Temporal use a per-instance set of handled ids. On InProc, the set lasts for
  the life of the instance. On Durable Task and Temporal, the set is for one generation, and it resets at
  continue-as-new.
- Restate uses a write-once durable promise. This makes the `raiseId` advisory.
- Elsa has no place in the flow to record handled ids. It routes the resume through a configured
  idempotency store.
- Camunda 8 / Zeebe removes duplicates by the broker message id, within the message TTL.

The library keeps the native behavior of each runtime and shows the differences in the matrix. One
emulated behavior on all runtimes would reduce each runtime to the weakest common behavior. It would
also hide edge cases that cause failures in production. The conformance test makes sure that the happy
path is the same on each runtime. The matrix documents the edges.

## What stays off the replay path

On the replay runtimes, the per-instance key mutation at the termination is non-deterministic. It must
run inside an activity, never inline in the workflow body. This is the most common error in a native
flow. The SoEx termination hooks do this for you. They are the Temporal interceptor and the termination
activity of the Durable Task base orchestrator. Configure the provided hook. Do not call the termination
yourself from flow code.

## See also

- [Runtime matrix](../reference/runtime-matrix.md): the per-runtime tables.
- [Consumption models](consumption-models.md): why the two models produce incompatible journals.
- [Author a native flow](../how-to/author-a-native-flow.md): the per-runtime recipes.
