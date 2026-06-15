# Multi-manager example — two managers, one workflow utility, one runtime

This example shows several business managers sharing a single `WorkflowUtility` on a single runtime, and a
"forget subject S" request being driven to crypto-shred through **the manager that owns each instance** rather
than one global contract.

```
dotnet run --project examples/MultiManager
```

## What it demonstrates

- **Multi-manager routing.** Two managers (Onboarding and Billing) share one utility's stores. The utility
  resolves each instance's owning manager from the instance-id prefix (`ErasureRouting.ByPrefix`) and drives
  erasure through that manager's `IErasureEvents` — so each manager only ever handles its own instances.
- **An asynchronous, durable front door.** `RequestEraseAsync` admits the request and returns immediately;
  a later `DrainEraseRequestsAsync` pass runs the erasure. The caller is never blocked for the shred, which is
  a statutory-deadline job, not a synchronous SLA.
- **A synchronous shred core.** The decoupling is at the request boundary only. The shred itself stays a
  single synchronous call into the owning manager, so the "retain-confirmed, then destroy" ordering holds —
  exactly the reason a queue is *not* placed between the utility and the manager (see
  [Why the sequence runs synchronously](../../docs/explanation/crypto-shred-and-erasure.md)).
- **Crypto-shred.** Each instance's payload is readable before the shred and unrecoverable after.

## How it is wired

The demo composes the governed core by hand (no SoEx host ceremony) to keep the focus on routing:

- one `InMemoryInstanceKeyStore` + `InMemorySubjectIndex` + `InMemoryPendingErasureRequests`, shared by both
  managers — one utility, one set of stores;
- a `WorkflowUtility` built with `resolveErasureFor: ErasureRouting.ByPrefix(...)` mapping each manager's flow
  prefix to its erasure contract, plus the `pending` intake store for the front door.

In a real system each manager is its own SoEx subsystem (its own entrypoint, gateway, and `GovernedTermination`
over the shared stores), and the composition supplies the same routing map to the utility. The natural
completion path is already per-manager by construction; only the utility's request-driven erase/sweep fan-out
needs the routing this example shows.
