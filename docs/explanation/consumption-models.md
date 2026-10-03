> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Consumption models

A consumption model sets the party that drives the flow of a SoEx.Workflow instance. There are two
models: the portable flow and the native flow. Both models use the same governed core. You choose one
model for each instance, and the instance keeps that model for its full life. For the procedure to
choose, see [Choose a consumption model](../how-to/choose-a-consumption-model.md).

## One governed core, two ways to drive it

Each SoEx.Workflow instance uses the governed core. The governed core has two parts:

- `GovernedStep<I>` runs one dispatch of your component through the SoEx host pipeline. It applies the
  per-step governance. It mints the per-instance key and indexes the subject. If the idempotency store
  is wired, it also collapses at-least-once redelivery to one effect.
- `GovernedTermination` runs the erasure lifecycle at the end. It extracts must-retain data, destroys
  the key, and prunes the subject index.

The consumption model sets the party that drives the flow around these calls. That party sets the order
of the steps, when to wait, and when to loop. The governance is the same machinery in both models. It
includes the keys, crypto-shred, the subject index, idempotency, and the erasure lifecycle.

## The portable flow: SoEx drives

In the portable flow, you write one component. Its step operation returns a
[`WorkflowAction`](../reference/workflow-action.md). `WorkflowAction` is a small vocabulary: "complete",
"go to the next step", "wait for an event", "delay", and "loop". SoEx supplies a generic driver for each
runtime. The driver owns the step loop:

1. It dispatches each step through the governed core.
2. It routes the returned action onto the durable primitives of the runtime.
3. It runs the termination when the flow completes.

The same component runs with no change on InProc, Durable Task, Temporal, Elsa, and Restate. You choose
the runtime when you host the component. Your code does not name the runtime. The flow can express only
what the `WorkflowAction` vocabulary can express.

The driver also owns the journaled bytes. The driver seals each payload that it persists with the
per-instance key. Thus the runtime receives only ciphertext, and crypto-shred is automatic in this
model. You write no encryption code.

## The native flow: the runtime drives

In the native flow, you write the flow in the model of the runtime. Examples are a Temporal
`[Workflow]` with parallel activities and child workflows, a Durable Task fan-out, an Elsa graph, and a
Camunda 8 BPMN diagram drawn in a visual editor. Your component runs each step and returns a business
result. A small hook for each runtime calls the governed termination at the end.

The native flow gives you all the features of the runtime. You write one flow for each runtime. You
also control what each step persists. Thus you own one governance duty that the driver owns in the
portable flow: the journal must hold only ciphertext. The rules are:

1. Seal the subject into an opaque seed.
2. Pass the seed from step to step.
3. Unseal only inside a step.

[Author a native flow](../how-to/author-a-native-flow.md) gives the procedure.

InProc has no native model of its own. Thus InProc always uses the portable flow.

## Why there's no migration

An instance uses one consumption model for its full life. The cause is the way durable execution works:

1. Each model writes a different durable journal with a different replay shape.
   - The portable flow journals a `WorkflowAction`-routed step loop with flattened action DTOs. On
     Restate, it uses a `/step`+`/terminate` wire contract.
   - A native flow journals the runtime-native flow with business-result steps. On Restate, it uses a
     `/gov-step`+`/gov-terminate` contract.
2. A durable runtime resumes an instance when it replays the history of the instance.
3. A driver can deterministically replay only a history that it wrote. No shared intermediate form
   exists to translate between the two journals.

To change the model, start a new instance under the other model. An instance cannot be upgraded in
place. One host can wire both models, for different instances. Decide at the start which property you need: the expressiveness of the
runtime or portability across runtimes.

## See also

- [Choose a consumption model](../how-to/choose-a-consumption-model.md) gives the procedure for the
  decision.
- [Runtimes and durability](runtimes-and-durability.md) explains why the replay shapes are different.
