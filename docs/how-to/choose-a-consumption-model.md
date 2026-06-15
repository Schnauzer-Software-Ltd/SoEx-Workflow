> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# How to choose a consumption model

SoEx.Workflow gives you two ways to consume it. You pick one per instance, and the choice is permanent
for that instance: there's [no migration](../explanation/consumption-models.md#why-theres-no-migration),
so switching means starting a fresh instance under the other model. This guide helps you decide.

## When to pick the portable flow

The portable flow is the right choice when you want one component that runs unchanged on every runtime,
your flow fits the [`WorkflowAction`](../reference/workflow-action.md) vocabulary (complete, route into
the next step, wait for an event with an optional timeout, delay, or loop/continue-as-new), and you'd
rather not learn or maintain a different flow model per backend.

You write the flow as values returned from your step operation, and SoEx's generic driver drives it.
See [Run the portable flow](run-the-portable-flow.md).

## When to pick a native flow

A native flow is the right choice when you need the full expressiveness of a particular backend
(Temporal's parallel activities and child workflows, a Durable Task fan-out, an Elsa graph, or a
Camunda 8 BPMN diagram you draw in a visual editor), when the flow is naturally visual or already
exists as a backend-native artifact, or when you're targeting Camunda 8 / Zeebe, which is native-only
because the BPMN graph itself is the flow.

You author the flow in the backend's own model and call the governed step from each step. See
[Author a native flow](author-a-native-flow.md).

## Either way, governance is identical

Both models build on the same governed core, so the per-instance key and crypto-shred, the subject
index, idempotency, and the erasure lifecycle behave the same regardless of which you pick. The only
difference is who drives the flow: SoEx's driver, or your backend-native code.

## At a glance

| | Portable flow | Native flow |
|---|---|---|
| You write | one component returning a `WorkflowAction` | a component returning a business result + the flow |
| The flow lives in | SoEx's generic driver | your backend code (or a BPMN diagram) |
| Runs unchanged on every runtime | yes | the component does; you write a flow per backend |
| Flow expressiveness | the `WorkflowAction` vocabulary | full backend power |
| Available on | InProc, Durable Task, Temporal, Elsa, Restate | Durable Task, Temporal, Elsa, Restate, Camunda 8/Zeebe |
| Governance | identical | identical |

For the reasoning behind the split, and why the two produce incompatible instances, see
[Consumption models](../explanation/consumption-models.md).
