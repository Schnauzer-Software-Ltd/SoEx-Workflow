> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Multi-manager example: two Managers, one workflow utility, one runtime

In this example, several business Managers share one `WorkflowUtility` on one runtime. A "forget subject
S" request goes to crypto-shred through the Manager that owns each instance. Each Manager has its own
erasure contract.

```
dotnet run --project examples/MultiManager
```

## What it demonstrates

- **Multi-manager routing.** Two Managers (Onboarding and Billing) share the stores of one utility. The
  utility finds the owner Manager of each instance from the prefix of the instance id
  (`ErasureRouting.ByPrefix`). It then runs the erasure through the `IErasureEvent` of that Manager. Thus
  each Manager handles only its own instances.
- **An asynchronous, durable entry point.** `RequestEraseAsync` accepts the erasure request and returns
  immediately. A later `DrainEraseRequestsAsync` pass runs the erasure. The caller does not wait for the
  shred. The shred has a statutory deadline. It has no synchronous service level.
- **A synchronous shred core.** The request boundary is asynchronous. The shred itself is one synchronous
  call into the owner Manager. Thus the order "confirm the retained data, then destroy" stays correct.
  For this reason, there is no queue between the utility and the Manager. See
  [Why the sequence runs synchronously](../../docs/explanation/crypto-shred-and-erasure.md).
- **Crypto-shred.** Before the shred, you can read the payload of each instance. After the shred, the
  payload is unrecoverable.

## How it is wired

The demo composes the governed core by hand, with no SoEx host setup. Thus the demo shows only the routing:

- One `InMemoryInstanceKeyStore`, one `InMemorySubjectIndex`, and one `InMemoryPendingErasureRequests`.
  Both Managers share them. There is one utility and one set of stores.
- A `WorkflowUtility` built with `resolveErasureFor: ErasureRouting.ByPrefix(...)`. This map connects the
  flow prefix of each Manager to its erasure contract. The utility also has the `pending` intake store for
  the entry point.

In a real system, each Manager is its own SoEx subsystem. It has its own entrypoint, its own gateway, and
its own `GovernedTermination` over the shared stores. The composition gives the same routing map to the
utility. The natural completion path is already separate for each Manager, by design. Only the erase and
sweep fan-out of the utility, which a request starts, needs the routing that this example shows.
