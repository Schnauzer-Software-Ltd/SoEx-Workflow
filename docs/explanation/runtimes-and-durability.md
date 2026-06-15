> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Runtimes and durability

SoEx.Workflow runs on six runtimes — five durable production engines (Durable Task, Temporal, Elsa,
Restate, Camunda 8 / Zeebe) plus in-process, which is for tests and demos and keeps no state across a
restart — and they don't all work the same way underneath. This page explains the durability models, how
the SoEx governance maps onto each, and why some behaviors legitimately differ between them. For the
tables, see the [runtime matrix](../reference/runtime-matrix.md).

## The governance is the constant

Start from what doesn't change. On every runtime, a governed step is invoked the same way (through the
SoEx endpoint pipeline into your component), and the same per-step governance (key, subject index,
idempotency) and the same termination lifecycle apply. Your step code is identical everywhere. What
changes is the flow around the steps and the durability mechanism underneath them.

## Four durability models

Durable execution means an instance survives process death and resumes. There are a few fundamentally
different ways to achieve that, and the runtimes split across them.

**Event-sourced replay** (Durable Task, Temporal). State is rebuilt by replaying a journal of events.
This has a sharp consequence: flow code must be deterministic, because it's re-executed on replay.
Anything non-deterministic (wall-clock reads, random ids, and above all the key-store mutation at the
termination) must run off the replay path, inside an activity. This is why the native-flow termination
always runs via an interceptor's activity rather than inline in the workflow.

**Checkpoint/resume** (Elsa). State is persisted at bookmarks and resumed from them. The flow parks on
a bookmark; an event resumes it. There's no replay, so determinism is less of a constraint, but resume
is driven by correlation rather than a re-run.

**Journalled, out-of-process** (Restate). The flow runs in a separate process (the Restate sidecar)
that journals each durable step and calls back into .NET over HTTP. The governance lives entirely on
the .NET side; Restate sees only ciphertext.

**Broker-journalled** (Camunda 8 / Zeebe). The broker owns the flow as a BPMN graph and journals
process variables. The .NET side is just job workers and a termination listener.

## How the model maps

Across all of them the mapping has the same shape (a flow, governed steps, a termination hook),
realized in each engine's primitives:

| | Flow is… | A step is… | The termination is… |
|---|---|---|---|
| Durable Task | an orchestration | a `CallActivity` → governed step | a base-orchestrator `finally` → termination activity |
| Temporal | a `[Workflow]` | an `[Activity]` → governed step | an interceptor-scheduled termination activity |
| Elsa | a registered graph | an activity → governed step | a termination activity in the graph |
| Restate | the Restate sidecar | a `ctx.run` → `/gov-step` callback | a final `ctx.run` → `/gov-terminate` callback |
| Zeebe | a BPMN diagram | a service-task job → governed step | a process end execution-listener job |
| InProc | the portable flow | a driver-driven dispatch | the driver's completion path |

The portable flow collapses the "flow" column into a single generic driver; the native flows let you
write each one in the engine's own idiom. InProc is in this table because it runs the same governed
step and termination, but it is **not** one of the four durability models above: it holds state in memory
only and nothing survives a restart, so it is for tests and demos, not production.

## Why the trigger semantics diverge

It would be convenient if start and raise behaved identically everywhere, but they can't, because the
engines have different models of identity and signaling.

A duplicate start means different things to different engines. Restate keys a workflow by a value that
runs once ever, so a second start is a silent no-op. Temporal rejects an already-started id. InProc
frees a completed id so it can be re-onboarded as a fresh generation. None of these is wrong; they're
different identity models.

A raise that arrives before its wait is armed is buffered on engines with durable signals (Temporal,
Durable Task), resolved into a promise on Restate, but rejected on Elsa, where a resume needs a bookmark
that doesn't exist yet.

Idempotent raises are deduped by a per-instance handled-id set on some engines, by a write-once durable
promise on Restate (which makes the `raiseId` advisory), and on Elsa, which has no in-flow place to
record handled ids, by routing the resume through a wired idempotency store.

The library's stance is to expose these differences plainly in the matrix rather than emulate a single
behavior everywhere, which would mean degrading every engine to the weakest common denominator, or
hiding edge cases that bite in production. The conformance test pins the happy path identical; the
matrix documents the edges.

## What stays off the replay path

The most common native-flow footgun bears repeating: on the replay engines, the per-instance key
mutation at the termination is non-deterministic and must run inside an activity, never inline in the
workflow body. SoEx's termination hooks do this for you (the Temporal interceptor, the Durable Task
base orchestrator's termination activity), which is why you wire the provided hook rather than calling
the termination yourself from flow code.

## See also

- [Runtime matrix](../reference/runtime-matrix.md) — the per-runtime tables.
- [Consumption models](consumption-models.md) — why the two models produce incompatible journals.
- [Author a native flow](../how-to/author-a-native-flow.md) — the per-runtime recipes.
