> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# How to choose a consumption model

SoEx.Workflow has two consumption models: the portable flow and the native flow. You choose one model
for each instance. An instance keeps its model for its full life. An instance cannot
[migrate to the other model](../explanation/consumption-models.md#why-theres-no-migration). To use the
other model, start a new instance. This guide helps you choose.

## When to pick the portable flow

Use the portable flow when all of these conditions are true:

- You want one component that runs on each runtime with no change.
- Your flow fits the [`WorkflowAction`](../reference/workflow-action.md) vocabulary. The actions are:
  complete, route into the next step, wait for an event with an optional timeout, delay, and
  loop/continue-as-new.
- You want one flow model for all runtimes.

In the portable flow, your step operation returns values that make the flow. The SoEx driver runs the
flow. See [Run the portable flow](run-the-portable-flow.md).

## When to pick a native flow

Use a native flow when one of these conditions is true:

- You need the full feature set of one runtime. Examples are Temporal parallel activities and child
  workflows, a Durable Task fan-out, an Elsa graph, and a Camunda 8 BPMN diagram that you draw in a
  visual editor.
- The flow is visual, or it already exists as an artifact of the runtime.
- Your runtime is Camunda 8 / Zeebe. Camunda 8 / Zeebe supports the native flow only, because the BPMN
  graph is the flow.

In a native flow, you write the flow in the model of the runtime. Each step of the flow calls the
governed step. See [Author a native flow](author-a-native-flow.md).

## Governance in the two models

Both models use the same governed core. These parts work the same in each model:

- the per-instance key and crypto-shred
- the subject index
- idempotency
- the erasure lifecycle

The two models differ in the part that drives the flow. In the portable flow, the SoEx driver drives
it. In a native flow, your runtime code drives it.

## At a glance

| | Portable flow | Native flow |
|---|---|---|
| You write | one component that returns a `WorkflowAction` | a component that returns a business result, and the flow |
| The flow is in | the SoEx driver | your runtime code or a BPMN diagram |
| Runs on each runtime with no change | yes | the component, yes; you write one flow for each runtime |
| Flow features | the `WorkflowAction` vocabulary | all the features of the runtime |
| Runtimes | InProc, Durable Task, Temporal, Elsa, Restate | Durable Task, Temporal, Elsa, Restate, Camunda 8/Zeebe |
| Governance | the same | the same |

[Consumption models](../explanation/consumption-models.md) gives the reasons for the two models. It
also tells why an instance of one model is not compatible with the other model.
