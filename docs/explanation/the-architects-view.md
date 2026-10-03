> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Explanation — the architect's view

This page is for an architect who has an IDesign Method system. In that system, a Manager fronts each
subsystem and calls Engines and ResourceAccess. A topology composes the subsystems. SoEx.Workflow adds
durable workflow and right-to-erasure to this architecture, and the architecture keeps its structure.
This page starts with the two parts that you wire: the Workflow utility and its binding. The other pages
of these docs build on these two parts, and this page links to them.

## A Method Utility

SoEx.Workflow is a reusable Method Utility: `WorkflowUtility`, in the
[`SoEx.Method.Workflow`](../reference/packages.md) package. It encapsulates one volatility: durable
governed step execution with right-to-erasure. This volatility stays in the utility and out of your
Managers. The utility holds no business logic and makes no business decisions. Its contract stays stable
while your business volatilities change. A Manager's logic can change, and the runtime can change, and
the contract of the utility stays the same.

## A sibling subsystem, reached by proxy

The utility is the entrypoint of a sibling subsystem next to yours. The example pairs a `membership`
subsystem with a `membership-workflow` subsystem. Your Manager calls the utility through a SoEx proxy. A
proxy is a cross-subsystem call that the framework resolves. Your Manager does not construct the utility
and does not receive it by constructor injection.

```
membership.Manager  ──proxy──▶  membership-workflow.WorkflowUtility   (start / raise event / recover subjects)
        ▲                                      │
        └──────────── proxy (IErasureEvent) ◀─┘   (drive crypto-shred back into your entrypoint)
```

The call goes across subsystems through the framework. It never goes sideways inside a subsystem. Thus
the closed architecture of the Method stays intact. The utility drives erasure back into your
entrypoint through a second framework proxy. This return proxy resolves through the channel that your
host already registered. Thus your composition root has one registration and no cycle.

## Two contracts for two callers

The utility exposes two contracts. They are distinct types because SoEx binds one channel for each
contract. The two contracts map to the Method split between business-triggered calls and operational
calls:

- `SubSystem.IWorkflowUtility` is the contract that a peer Manager calls through a proxy to drive a
  flow:
  - `StartAsync` seals a first step and starts the instance.
  - `RaiseEventAsync` raises a business event on a waiting instance.
  - `SubjectsForAsync` recovers the subjects that are still mapped to an instance, for example for a
    must-retain carve-out.
  - `InstancesForAsync` does the same index read in reverse, scoped to one flow key.
- `External.IWorkflowUtility` is the contract that the host or the ingress calls as a system client:
  - `RequestEraseAsync` admits a right-to-erasure request.
  - `DrainEraseRequestsAsync` is the pass that shreds the request.
  - `SweepAbandonedAsync`, `ReDriveHeldAsync`, and `ReviewDeadlinesAsync` are backstops that are
    independent of requests.

The utility owns the logic of the `External` operations. The host owns their cadence: a timer or a
scheduler calls them. Your Managers see only the first contract. Your composition root and your
operational schedule see the second contract.

A Manager usually needs no reverse lookup. `DeterministicInstanceId` derives an instance id again from
business identity, with no store. `InstancesForAsync` is for the subjects that derivation cannot reach.
A step can declare a person on the action that it returns. The instance then holds subjects that it did
not start with, and no id can be derived from those subjects. `InstancesForAsync` is scoped to a flow
key because the index spans all flows and all Managers that share the utility. A Manager must not
receive the instance ids of another Manager.

## The binding connects your entrypoint to the runtime

The subsystem entrypoint whose operation is a workflow step is hosted on a `WorkflowBinding<I>`. This
binding takes the place of a plain in-process binding. You put it in your topology as any other
binding. At process startup, the host resolves the endpoint of that binding. Then, through the
`WorkflowSeam`, the host connects these parts for each flow:

- the engine-agnostic `IWorkflowGateway`, which starts and raises on the chosen runtime
- the `WorkflowSealer`
- the governed step and the governed termination.

Call `WorkflowSeam.Connect(flowKey, …)` one time for each flow that you host.

The runtime is a volatility that the gateway and the binding hide. The runtime is Temporal, Durable
Task, Elsa, Restate, Camunda 8 / Zeebe, or InProc. You choose it at composition. When you change the
runtime, no Manager changes. [The governed core](../reference/governed-core.md) gives the full wiring
sequence: resolve the endpoint, build the governed step and the governed termination, and connect the
seam.

## Your Manager is a plain SoEx component

Each governed step runs your operation through the SoEx host pipeline: endpoint, then dispatcher, then
your operation. Thus the component opens no envelope, holds no workflow state, and names no runtime. It
is the component that the Method tells you to write: one typed input and one typed result.

The location of the flow is the one architectural choice that you make for each instance:

- In a native flow, you write the flow in the model of the runtime: a Temporal `[Workflow]`, an Elsa
  graph, or a BPMN diagram. Your operation returns a business result.
- In the portable flow, your operation returns a `WorkflowAction`. A driver from SoEx runs the step
  loop. Thus one component runs with no change on each runtime that supports the portable flow.

The per-step governance is the same in both models: key mint, subject index, idempotency, and
termination. [Consumption models](consumption-models.md) describes the trade-off between the
expressiveness of the runtime and one component for all runtimes.

## Right-to-erasure is a contract of your entrypoint

Your entrypoint implements `IErasureEvent`:

- `OnRetaining` extracts must-retain data.
- `OnTerminated` does the bookkeeping after the shred.
- `OnRetentionHeld` is the quarantine for a failed extraction.

The utility drives crypto-shred through this contract at the end of each instance. The `External`
contract turns "forget subject S" into one system operation that your host schedules. Thus erasure is
one mechanism. The utility owns the mechanism, and your contract supplies its parameters. No Manager
implements erasure again. [Crypto-shred and erasure](crypto-shred-and-erasure.md) describes the model.
[The erasure API](../reference/erasure-api.md) and
[run erasure maintenance](../how-to/run-erasure-maintenance.md) describe the operations.

## The volatility map

| Concern | Encapsulated in | Owner and volatility |
|---|---|---|
| The business decisions of the flow | your Manager (the step component) | yours; the volatile part |
| Durable execution and the erasure mechanism | `WorkflowUtility` (a Utility, its own subsystem) | provided; stable when your code changes |
| The durable runtime | the `IWorkflowGateway` behind the `WorkflowBinding` | hidden; chosen one time at composition |
| Key store · subject index · idempotency | the [governance services](../reference/governance-services.md) that you supply | replaceable resources (in-memory or durable) |

Keep this shape: business logic in your Managers, durability and erasure in the utility, the runtime
behind the binding, and persistence in the resources that you supply. Each concern in the first column
is independent of the other concerns.

## Where to read next

- Wire it: [the governed core](../reference/governed-core.md) and [packages](../reference/packages.md).
- Choose a model: [consumption models](consumption-models.md), then
  [run the portable flow](../how-to/run-the-portable-flow.md) or
  [author a native flow](../how-to/author-a-native-flow.md).
- Trigger it from outside with webhooks, deterministic ids, and the authorization point:
  [the triggering seam](the-triggering-seam.md).
- Make it durable in production: [make crypto-shred durable](../how-to/make-crypto-shred-durable.md).
- See it run and reproduce the behavior: the [`examples/`](../../examples) and
  [verify it yourself](../how-to/verify-it-yourself.md).
