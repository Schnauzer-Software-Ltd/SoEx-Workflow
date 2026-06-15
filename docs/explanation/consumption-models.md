> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Consumption models

SoEx.Workflow offers two ways to consume it. This page covers why there are two, what they share, and
why an instance can't move between them. For the practical decision, see
[Choose a consumption model](../how-to/choose-a-consumption-model.md).

## One governed core, two ways to drive it

Every SoEx.Workflow instance is built on the same governed core: a governed step and a governed
termination. `GovernedStep<I>` runs one dispatch of your component through the SoEx host pipeline,
applying per-step governance: minting the per-instance key, indexing the subject, and (when wired)
collapsing at-least-once redelivery to a single effect. `GovernedTermination` runs the erasure lifecycle
at the end: extract must-retain data, destroy the key, prune the subject index.

The only thing the two models differ on is who drives the flow around those calls: who decides the
order of steps, when to wait, when to loop. The governance (keys, crypto-shred, the subject index,
idempotency, the erasure lifecycle) is the same machinery either way, byte for byte.

## The portable flow: SoEx drives

In the portable model you write one component whose step operation returns a
[`WorkflowAction`](../reference/workflow-action.md), a small vocabulary of "complete", "go to the next
step", "wait for an event", "delay", "loop". SoEx ships a generic per-backend driver that owns the step
loop: it dispatches each step through the governed core, routes your returned action onto the backend's
durable primitives, and runs the termination on completion.

The payoff is portability. The same component runs unchanged on InProc, Durable Task, Temporal, Elsa,
and Restate; you pick the runtime at hosting time, not in your code. The cost is expressiveness: your
flow is whatever the `WorkflowAction` vocabulary can say.

The driver also owns the journaled bytes, which is why crypto-shred is automatic in this model. The
driver seals every payload it persists under the per-instance key, so the backend only ever sees
ciphertext, and you write no encryption code.

## The native flow: your backend drives

In the native model you author the flow in the backend's own model: a Temporal `[Workflow]` with
parallel activities and child workflows, a Durable Task fan-out, an Elsa graph, a Camunda 8 BPMN diagram
drawn in a visual editor. Your component just runs each step and returns a business result; a small
per-backend hook calls the governed termination at the end.

The payoff is the full power of the backend. The cost is that you write a flow per backend, and, because
you now control what each step persists, you take on one governance duty the portable flow handles for
you: journaling only ciphertext. The discipline (seal the subject into an opaque seed, thread the seed,
unseal only inside a step) is covered in [Author a native flow](../how-to/author-a-native-flow.md).

InProc has no native backend of its own, so it is always portable.

## Why there's no migration

You choose one model per instance, and the choice is permanent for that instance. This is a consequence
of how durable execution works rather than a policy decision.

Each model produces a different durable journal and replay shape. The portable flow journals a
`WorkflowAction`-routed step loop with flattened action DTOs (and, on Restate, a `/step`+`/terminate`
wire contract). A native flow journals the backend-native flow with business-result steps (and a
`/gov-step`+`/gov-terminate` contract). Durable engines resume an instance by replaying its history, and
one driver cannot deterministically replay a history the other driver wrote. There is no shared
intermediate form to translate between them.

So "switching models" means starting a fresh instance under the other model; there's no in-place
upgrade. In practice this is rarely a constraint. A single host wires one model, and you decide up front
based on whether you need backend expressiveness or cross-runtime portability.

## See also

- [Choose a consumption model](../how-to/choose-a-consumption-model.md) — the practical decision.
- [Runtimes and durability](runtimes-and-durability.md) — why replay shapes differ in the first place.
