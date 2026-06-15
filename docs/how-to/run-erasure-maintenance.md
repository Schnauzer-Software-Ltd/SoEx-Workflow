> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# How to run erasure maintenance

The termination hook crypto-shreds an instance on its happy paths (completion, timeout, compensation).
But some instances are abandoned before their hook ever runs: a hard worker death at the termination
instant, or an admin `terminate`/`purge` that bypasses flow code. Some erasures also get held
(extraction failed) or are owed a deadline. Three maintenance passes close those gaps over time, and
this guide shows how to run them.

> For what each pass is and the full API, see the [erasure API reference](../reference/erasure-api.md).

## The three passes

| Pass | Closes | One-pass call |
|---|---|---|
| Sweep abandoned | instances whose subject never files a request | `coordinator.SweepAsync(olderThan, resolve)` |
| Re-drive held | erasures quarantined when `OnRetaining` failed past retry | `coordinator.ReDriveHeldAsync(...)` |
| Review deadlines | instances left to complete naturally whose statutory window is closing | `coordinator.ReviewDeadlinesAsync(...)` |

A "forget subject S" request already re-drives any still-indexed, un-terminated instance for that
subject, so a filed request closes the gap whenever the subject's data is erased. The passes above are
for everything no one files a request for.

## The sweep

The sweep enumerates the live (un-shredded) key set and force-terminates every instance whose key was
minted longer than `olderThan` ago. `ErasureSweepLoop` runs it on a timer:

```csharp
// sweep every hour, shredding anything abandoned for over a day
await new ErasureSweepLoop(coordinator, olderThan: TimeSpan.FromDays(1), resolve)
    .RunAsync(interval: TimeSpan.FromHours(1), cancellation: stoppingToken);
```

> `olderThan` is purely an age threshold; the sweep does not probe liveness. Set it longer than your
> longest legitimate flow, or a still-running instance will be mistaken for an abandoned one.

The sweep needs an `IEnumerableInstanceKeyStore` (the bundled in-memory, OpenBao, and RavenDB stores
all are); a key store that can't enumerate doesn't offer this backstop.

## Right-to-erasure: admit and drain

Erasure is admit-and-drain — there is no synchronous variant:

- `RequestEraseAsync(subject)` records the request and returns its id at once, **without shredding**.
- `DrainEraseRequestsAsync()` is the pass that processes admitted requests, driving each to crypto-shred
  through its owning manager. It is one of the maintenance passes — the built-in runner drives it by default,
  and a dedicated scheduler should too.

> **Admitting a request is not erasing it.** `RequestEraseAsync` only acknowledges; nothing is shredded until a
> drain runs. You **must** schedule `DrainEraseRequestsAsync` on a cadence, and within your statutory deadline.
> An admitted request that is never drained is never honoured.

> **A pending store is required — and must be durable in production.** Erasure is admit-and-drain, so the
> utility needs an `IPendingErasureRequests` store: `RequestEraseAsync` / `DrainEraseRequestsAsync` throw if one
> is not wired. The in-memory default works for a single process but loses admitted-but-undrained requests on
> restart, so a request you acknowledged would be lost before it was acted on; production uses the shipped
> durable store — the *same* store as the request registry below (`RavenDbErasureRequestRegistry` /
> `EfCoreErasureRequestRegistry` implement both), so one connection serves both. An instance started for the
> subject *after* the admit is not in that request; the sweep and a re-filed request are its backstops.

Watch the backlog so an unscheduled or stalled drain is caught before it breaches a deadline:
`IPendingErasureRequests.Backlog()` returns the admitted-but-undrained count and the oldest admit time. Age the
oldest against your statutory window from a health check or the drain scheduler — it is the pre-drain analogue
of the deadline review, and the alarm that the drain has stopped keeping up.

## Run them all: the built-in runner

A dependency-free runner drives the drain and the three backstops on their own cadences. Enable it with one option:

```csharp
_ = WorkflowMaintenance.RunAsync(utility, new WorkflowMaintenanceOptions { Enabled = true }, stoppingToken);
```

The drain is on by default — it is what crypto-shreds a filed request, so without it erasure never completes.
This runs in process with no leader election. That's fine for a single instance or dev, and safe (if
not correct-once) on several, since terminations are idempotent.

## Production: host a dedicated scheduler

For production or high availability, leave the built-in runner off and host a dedicated scheduler
separately (Quartz.NET, Hangfire, TickerQ, or whatever you already run) that calls the utility's
one-pass operations (`DrainEraseRequestsAsync` / `SweepAbandonedAsync` / `ReDriveHeldAsync` /
`ReviewDeadlinesAsync`) on a cadence, so exactly one node drives each pass. The drain is not optional — it is
the erasure path, so schedule it within your statutory deadline.

Wire the maintenance logs to a durable backend so that scheduler sees the same state across the
fleet:

- `IHeldInstanceRegistry` — written by the termination when an instance is held.
- `IErasureRequestRegistry` — written by `EraseAsync`; the open requests.
- `IPendingErasureRequests` — written by `RequestEraseAsync`; requests admitted via the async front door,
  awaiting a drain. The same durable store as the request registry implements it (one connection serves both).

All have in-memory defaults and shipped durable RavenDB and EF Core implementations.

The durable request registries take the same subject protector the [durable subject index](make-crypto-shred-durable.md)
uses. The subjects of a person exercising erasure are stored only as the protector's one-way token, with
no recoverable subject id at rest, so a backup, tombstone or freelist page of the request store cannot
leak who asked to be erased. Deadline review routes by instance id, so the registry never needs the
plaintext subject back. Pass the protector when you construct a durable registry:

```csharp
var protector = new HmacSubjectProtector(subjectTokenSecret); // the same secret the durable index uses
IErasureRequestRegistry requests = new EfCoreErasureRequestRegistry(maintenanceDbOptions, protector);
// or: new RavenDbErasureRequestRegistry(documentStore, protector);
```

Since the maintenance logs and the [subject index](make-crypto-shred-durable.md) are always wired
together, you can back all of them from one store with the bundle — `ErasureStores` (one EF Core
database) or `RavenErasureStores` (one document store) — which hands out the held registry, the request
registry, and the subject index over a single connection. See
[Back the index and maintenance from one store](make-crypto-shred-durable.md#optional-back-the-index-and-maintenance-from-one-store).

## Reference

- [Erasure API](../reference/erasure-api.md) — `ErasureCoordinator`, the passes, the logs,
  `WorkflowMaintenance`.
- [Make crypto-shred durable](make-crypto-shred-durable.md) — the durable stores the sweep needs.
