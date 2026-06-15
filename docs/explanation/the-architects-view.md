> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Explanation — the architect's view

This page is for the architect who already has an IDesign Method system (subsystems
fronted by a Manager, calling Engines and ResourceAccess, composed in a topology) and wants durable
workflow with right-to-erasure without bending that architecture. It starts where you'd start: the
Workflow utility and the binding you wire. Everything else in these docs is downstream of those two
pieces, and this page links out to it as it goes.

## It's a Utility, not a Manager

SoEx.Workflow is consumed as a reusable Method Utility: `WorkflowUtility`, in the
[`SoEx.Method.Workflow`](../reference/packages.md) package. It encapsulates exactly one volatility,
durable governed step execution with right-to-erasure, so your Managers never grow it. It holds no
business logic and makes no business decisions. Like any well-placed utility it stays
still while your business volatilities move: a Manager's logic can change, and the runtime underneath
can change, without touching the utility's contract.

## It is its own subsystem, reached as a proxy

The utility is the entrypoint of a sibling subsystem, alongside yours (the example pairs a `membership`
subsystem with a `membership-workflow` subsystem). Your Manager does not construct it or take it by
constructor injection. It reaches it as a SoEx proxy, a cross-subsystem call resolved by the framework:

```
membership.Manager  ──proxy──▶  membership-workflow.WorkflowUtility   (start / raise event / recover subjects)
        ▲                                      │
        └──────────── proxy (IErasureEvents) ◀─┘   (drive crypto-shred back into your entrypoint)
```

This keeps the Method's closed architecture intact: the call goes across through the framework, never
sideways inside the subsystem. The return leg, where the utility drives erasure back into your
entrypoint, is also a framework proxy, resolved through the same channel your host already registered,
so there is no second registration and no cycle in your composition root.

## Two faces, two callers

The utility exposes two contracts (deliberately distinct types, because SoEx binds one channel per
contract), and they map onto the Method's split between business-triggered and operational calls:

- `SubSystem.IWorkflowUtility` is what a peer Manager proxies to, to drive a flow: `StartAsync` (seal a
  first step and start the instance), `RaiseEventAsync` (raise a business event onto a waiting
  instance), and `SubjectsForAsync` (recover the subjects still mapped to an instance, for example for a
  must-retain carve-out).
- `External.IWorkflowUtility` is what the host or ingress calls as a system client: `RequestEraseAsync`
  (admit a right-to-erasure request) and `DrainEraseRequestsAsync` (the pass that shreds it), plus the
  request-independent backstops `SweepAbandonedAsync`, `ReDriveHeldAsync`, and `ReviewDeadlinesAsync`. The
  utility owns the logic; the host owns the cadence (a timer or scheduler invokes these).

Your Managers only ever see the first face; your composition root and operational schedule see the
second.

## The binding is where your entrypoint meets the runtime

The subsystem entrypoint whose operation is a workflow step is hosted on a `WorkflowBinding<I>` instead
of a plain in-process binding. You place it in your topology like any other binding. At process startup
the host resolves that binding's endpoint and, through the `WorkflowSeam`, connects per flow the
engine-agnostic `IWorkflowGateway` (start/raise on the chosen backend), the `WorkflowSealer`, and the
governed step and termination. One `WorkflowSeam.Connect(flowKey, …)` per flow you host.

The runtime (Temporal, Durable Task, Elsa, Restate, Camunda 8 / Zeebe, or in-process) is a volatility
hidden behind the gateway and the binding. You choose it at composition; no Manager changes when you
swap it. The exact wiring sequence (resolve the endpoint, build the governed step and termination,
connect the seam) is written out in [the governed core](../reference/governed-core.md).

## Your Manager stays a plain SoEx component

Each governed step runs your operation through the SoEx host pipeline (endpoint → dispatcher → your
operation), so the component cracks no envelope, holds no workflow state, and names no runtime. It is
the same component the Method would have you write: one typed input, one typed result.

Where the flow itself lives is the one architectural choice you make per instance. In a native flow you
author the flow in the backend's own model (a Temporal `[Workflow]`, an Elsa graph, a BPMN diagram), and
your operation returns a business result. In the portable flow your operation returns a `WorkflowAction`
and a SoEx-provided driver runs the step loop, so one component runs unchanged on every backend.

The per-step governance (key mint, subject index, idempotency, termination) is identical either way.
The trade-off (backend expressiveness vs one component everywhere) is the subject of
[consumption models](consumption-models.md).

## Right-to-erasure is a contract your entrypoint implements

Your entrypoint implements `IErasureEvents`: `OnRetaining` to extract must-retain data, `OnTerminated`
for post-shred bookkeeping, `OnRetentionHeld` for the extraction-failure quarantine. The utility drives
crypto-shred through it at the end of every instance, and the `External` face turns "forget subject S"
into a single system operation your host schedules. Erasure is therefore one mechanism, owned by the
utility and parameterised by your contract, rather than something each Manager re-implements. The model is in
[crypto-shred and erasure](crypto-shred-and-erasure.md); the operations are in
[the erasure API](../reference/erasure-api.md) and [run erasure maintenance](../how-to/run-erasure-maintenance.md).

## The volatility map

| Concern | Encapsulated in | Whose, and how volatile |
|---|---|---|
| The flow's business decisions | your Manager (the step component) | yours; the volatile part |
| Durable execution + erasure mechanism | `WorkflowUtility` (a Utility, its own subsystem) | provided; stable across your changes |
| Which durable runtime | the `IWorkflowGateway` behind the `WorkflowBinding` | hidden; chosen once at composition |
| Key store · subject index · idempotency | the [governance services](../reference/governance-services.md) you supply | swappable resources (in-memory or durable) |

The shape to keep in mind: business logic in your Managers, durability and erasure in the utility, the
runtime behind the binding, persistence in the resources you plug in. Nothing in the first column needs
to know anything in the others.

## Where to read next

- Wire it: [the governed core](../reference/governed-core.md) and [packages](../reference/packages.md).
- Pick a model: [consumption models](consumption-models.md), then
  [run the portable flow](../how-to/run-the-portable-flow.md) or
  [author a native flow](../how-to/author-a-native-flow.md).
- Trigger it from outside (webhooks, deterministic ids, the authorization chokepoint):
  [the triggering seam](the-triggering-seam.md).
- Make it durable in production: [make crypto-shred durable](../how-to/make-crypto-shred-durable.md).
- See it run and reproduce the behaviour: the [`examples/`](../../examples) and
  [verify it yourself](../how-to/verify-it-yourself.md).
