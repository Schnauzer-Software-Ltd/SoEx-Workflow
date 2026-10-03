> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# How to run erasure maintenance

Erasure maintenance completes the erasures that the termination hook does not complete. The termination
hook crypto-shreds an instance on its normal paths: completion, timeout, and compensation. Other
instances need maintenance:

- **Abandoned instances.** The termination hook of these instances did not run. Two causes are a hard
  worker death at the instant of termination, and an admin `terminate` or `purge` that skips flow code.
- **Held erasures.** The extraction failed, and the erasure went to held state.
- **Erasures with a deadline.** These erasures must complete before a statutory deadline.

Three backstop passes close these gaps over time. A fourth pass, the drain, does the crypto-shred for
each filed erasure request. This guide shows how to run the four passes.

> For a description of each pass and the full API, see the
> [erasure API reference](../reference/erasure-api.md).

## The four passes

Each pass has a one-pass call on two surfaces:

- `ErasureCoordinator` (`SoEx.Workflow`) is the core type. Its calls take a `resolve` function and return
  a report.
- The external face of the workflow utility (`SoEx.Method.Workflow.External.IWorkflowUtility`) wraps the
  coordinator. Its calls take the ages in seconds and return a count. The built-in runner and a dedicated
  scheduler call this surface.

| Pass | Closes | `ErasureCoordinator` call | Utility call |
|---|---|---|---|
| Drain | Filed erasure requests that wait for their crypto-shred. | `EraseInstancesAsync(request, instanceIds, resolve)`, one call for each admitted request | `DrainEraseRequestsAsync()` |
| Sweep abandoned | Instances for a subject that files no erasure request. | `SweepAsync(olderThan, resolve)` | `SweepAbandonedAsync(olderThanSeconds)` |
| Re-drive held | Erasures in quarantine because `OnRetaining` failed after all retries. | `ReDriveHeldAsync(resolve)` | `ReDriveHeldAsync()` |
| Review deadlines | Instances that complete naturally, with a statutory window that closes soon. | `ReviewDeadlinesAsync(escalateWithin, resolve)` | `ReviewDeadlinesAsync(escalateWithinSeconds)` |

A "forget subject S" request re-drives each indexed instance for that subject that has not terminated.
Thus a filed request closes the gap when the framework erases the data of the subject. The three
backstop passes are for the instances that have no filed request.

## The sweep

The sweep enumerates the live key set. The live key set holds the keys that the framework has not
shredded. The sweep force-terminates each instance with a key older than `olderThan`.
`ErasureSweepLoop` runs `ErasureCoordinator.SweepAsync` on a timer. Use it when you host the sweep
without the built-in runner.

1. Create an `ErasureSweepLoop` with the coordinator, the `olderThan` age, and the resolver.
2. Call `RunAsync` with the interval and the cancellation token.

```csharp
// sweep every hour, shredding anything abandoned for over a day
await new ErasureSweepLoop(coordinator, olderThan: TimeSpan.FromDays(1), resolve)
    .RunAsync(interval: TimeSpan.FromHours(1), cancellation: stoppingToken);
```

> [!WARNING]
> Set `olderThan` longer than your longest legitimate flow. `olderThan` is an age threshold only. The
> sweep does not check if an instance is alive. Thus the sweep can terminate a running instance as an
> abandoned instance.

The sweep needs an `IEnumerableInstanceKeyStore`. The bundled in-memory, OpenBao, and RavenDB key stores
all implement it. A key store that cannot enumerate its keys cannot support the sweep.

## Right-to-erasure: admit and drain

Erasure has two phases, admit and drain. There is no synchronous erasure.

- `RequestEraseAsync(subject)` records the erasure request and returns its id immediately. It does not
  shred.
- `DrainEraseRequestsAsync()` processes the admitted requests. It drives each request to crypto-shred
  through the Manager that owns it. The drain is one of the four maintenance passes. The built-in runner
  runs it by default. A dedicated scheduler must also run it.

> [!WARNING]
> Schedule `DrainEraseRequestsAsync` within your statutory deadline. `RequestEraseAsync` only
> acknowledges a request. The framework shreds nothing until a drain runs. If a drain never processes an
> admitted request, the framework never honours it.

> [!WARNING]
> Use a durable pending store in production. The erasure utility needs an `IPendingErasureRequests`
> store. If you do not wire one, `RequestEraseAsync` and `DrainEraseRequestsAsync` throw. The in-memory
> default works for a single process. On a restart, it loses the requests that the drain has not
> processed. Thus the framework can lose a request that you acknowledged.

The shipped durable store for pending requests is the same store as the request registry below.
`RavenDbErasureRequestRegistry` and `EfCoreErasureRequestRegistry` implement both interfaces. One
connection serves both.

An admitted request does not include an instance that starts for the subject after the admit. The sweep
and a request that you file again are the backstops for that instance.

### Monitor the backlog

Monitor the backlog to find a drain that has no schedule or that has stopped. You then find it before it
breaches a deadline.

1. Call `IPendingErasureRequests.Backlog()`. It returns the number of requests that the drain has not
   processed, and the oldest admit time.
2. Compare the age of the oldest request with your statutory window. Do this from a health check or from
   the drain scheduler.

This check is the equivalent of the deadline review before the drain. It is the alarm for a drain that
is too slow.

## Run all passes with the built-in runner

A runner with no dependencies runs the four passes, each on its own schedule. These are the defaults in
`WorkflowMaintenanceOptions`:

| Pass | Switch | Default interval |
|---|---|---|
| Drain | `Drain` | 1 minute (`DrainInterval`) |
| Sweep abandoned | `Sweep` | 15 minutes (`SweepInterval`). `SweepOlderThan` is 1 day. |
| Re-drive held | `ReDriveHeld` | 5 minutes (`HeldReDriveInterval`) |
| Review deadlines | `ReviewDeadlines` | 5 minutes (`DeadlineReviewInterval`). `EscalateWithin` is 1 day. |

Each switch is on by default.

1. Set `Enabled = true` in `WorkflowMaintenanceOptions`.
2. Call `WorkflowMaintenance.RunAsync` with the utility, the options, and the cancellation token.

```csharp
_ = WorkflowMaintenance.RunAsync(utility, new WorkflowMaintenanceOptions { Enabled = true }, stoppingToken);
```

The drain is on by default. The drain crypto-shreds a filed request. Without the drain, erasure never
completes.

The runner runs in process and has no leader election. It is correct for a single instance or for dev.
On several instances it is safe, because terminations are idempotent. It does not guarantee that each
pass runs once only.

## Host a dedicated scheduler for production

For production or high availability, use a dedicated scheduler.

1. Keep the built-in runner off.
2. Host a dedicated scheduler as a separate process. Examples are Quartz.NET, Hangfire, TickerQ, or the
   scheduler that you already run.
3. Make the scheduler call the one-pass operations of the utility on a schedule:
   `DrainEraseRequestsAsync`, `SweepAbandonedAsync`, `ReDriveHeldAsync`, and `ReviewDeadlinesAsync`.
4. Make sure that only one node runs each pass.
5. Schedule the drain within your statutory deadline.

The drain is the erasure path. It is mandatory.

### Wire the maintenance logs to a durable store

The scheduler must see the same state across the fleet. Thus wire the maintenance logs to a durable
store:

- `IHeldInstanceRegistry`: the termination writes to it when an instance goes to held state.
- `IErasureRequestRegistry`: `EraseAsync` writes to it. It holds the open requests.
- `IPendingErasureRequests`: `RequestEraseAsync` writes to it. It holds the requests that the async front
  door admitted and that wait for a drain. The durable store of the request registry implements it. One
  connection serves both.

Each log has an in-memory default. Each log also has shipped durable implementations for RavenDB and
EF Core.

The durable request registries use the same subject protector as the
[durable subject index](make-crypto-shred-durable.md). The registry stores the subjects of a person who
asks for erasure only as the one-way token of the protector. It keeps no recoverable subject id at rest.
Thus a backup, a tombstone, or a freelist page of the request store cannot show who asked for erasure.
The deadline review routes by instance id. Thus the registry never needs the plaintext subject.

1. Create the `HmacSubjectProtector` with the same secret as the durable index.
2. Pass the protector to the constructor of the durable registry.

```csharp
var protector = new HmacSubjectProtector(subjectTokenSecret); // the same secret the durable index uses
IErasureRequestRegistry requests = new EfCoreErasureRequestRegistry(maintenanceDbOptions, protector);
// or: new RavenDbErasureRequestRegistry(documentStore, protector);
```

You always wire the maintenance logs and the [subject index](make-crypto-shred-durable.md) together. Thus
you can keep all of them in one store with a bundle. `ErasureStores` uses one EF Core database.
`RavenErasureStores` uses one document store. The bundle gives the held registry, the request registry,
and the subject index over one connection. See
[Back the index and maintenance from one store](make-crypto-shred-durable.md#optional-back-the-index-and-maintenance-from-one-store).

## Reference

- [Erasure API](../reference/erasure-api.md): `ErasureCoordinator`, the passes, the logs, and
  `WorkflowMaintenance`.
- [Make crypto-shred durable](make-crypto-shred-durable.md): the durable stores that the sweep needs.
