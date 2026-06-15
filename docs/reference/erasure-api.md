> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Reference — erasure API

The types for driving a "forget subject S" request and the maintenance passes that close gaps over time.
Namespace: `SoEx.Workflow`. For task-level guidance see
[Run erasure maintenance](../how-to/run-erasure-maintenance.md).

## `ErasureCoordinator`

```csharp
public ErasureCoordinator(
    ISubjectIndex index,
    StatutoryDeadlineClock clock,
    ErasurePlanner planner,
    TerminationCoordinator termination,
    ErasureReporter reporter,
    IEnumerableInstanceKeyStore? liveInstances = null,
    TimeProvider? time = null,
    IHeldInstanceRegistry? heldRegistry = null,
    IErasureRequestRegistry? requestRegistry = null);
```

| Member | Description |
|---|---|
| `Task<ErasureResult> EraseAsync(ErasureRequest request, Func<string, ErasureTarget?> resolve)` | Run a request end to end: fan out across the index, decide per instance, drive terminations to crypto-shred (or quarantine), report. `resolve` maps an instance id to its `ErasureTarget` (or `null` if unresolvable). |
| `Task<SweepReport> SweepAsync(TimeSpan olderThan, Func<string, ErasureTarget?> resolve)` | Request-independent backstop: force-terminate every live instance whose key was minted longer than `olderThan` ago. Needs an `IEnumerableInstanceKeyStore`. |
| `Task<ReDriveReport> ReDriveHeldAsync(Func<string, ErasureTarget?> resolve)` | One pass over held (quarantined) instances, retrying their extraction. Requires an `IHeldInstanceRegistry`. |
| `Task<DeadlineReviewReport> ReviewDeadlinesAsync(TimeSpan escalateWithin, Func<string, ErasureTarget?> resolve, Func<DeadlineEscalation, Task>? onEscalate = null)` | One pass over open requests whose statutory window is closing, force-terminating instances that would breach. Requires an `IErasureRequestRegistry` and an `IEnumerableInstanceKeyStore`. |

## Request and result types

```csharp
public sealed record ErasureRequest(string RequestId, DateTimeOffset ReceivedAt, IReadOnlyList<string> Subjects)
{
    public static ErasureRequest For(string requestId, DateTimeOffset receivedAt, params string[] subjects);
}

public sealed record ErasureTarget(
    string InstanceId,
    IErasureEvents Contracts,
    IdempotencyKey IdempotencyKey,
    TimeSpan? MaxRemainingDuration);   // null = unbounded → force-terminate

public sealed record InstanceErasureOutcome(string InstanceId, ErasureAction Action, ErasureState State);

public sealed record ErasureResult(
    ErasureRequest Request,
    DeadlineStatus Deadline,
    ErasureReport Report,
    IReadOnlyList<InstanceErasureOutcome> Outcomes);

public sealed record SweepReport(IReadOnlyList<InstanceErasureOutcome> Outcomes)
{
    public int Swept { get; }   // Outcomes where State == Complete
    public int Held  { get; }   // Outcomes where State == Held
}
```

- `ErasureRequest.ReceivedAt` anchors the statutory clock.
- `ErasureAction`: `CompleteNaturally` (bounded, self-erases before the deadline) | `ForceTerminate`.
- `ErasureState`: `InProgress` | `Complete` (clean shred) | `Held` (extraction failed → quarantine).
- `DeadlineStatus.EscalateBreachRisk` flags a request at risk of missing its statutory deadline.

## Collaborators

| Type | Constructor |
|---|---|
| `StatutoryDeadlineClock` | `(IDeadlinePolicy? policy = null, TimeSpan? escalateWithin = null, TimeProvider? time = null)` — null policy → a conservative default window |
| `ErasurePlanner` | `(TimeProvider? time = null)` |
| `ErasureReporter` | `()` |
| `TerminationCoordinator` | `(IInstanceKeyStore keys, ISubjectIndex index, int maxRetainingAttempts = 3, Func<int, Task>? backoffDelay = null, IHeldInstanceRegistry? heldRegistry = null)` |

## `ErasureSweepLoop`

Runs `SweepAsync` on a timer.

```csharp
public ErasureSweepLoop(
    ErasureCoordinator coordinator,
    TimeSpan olderThan,
    Func<string, ErasureTarget?> resolve,
    TimeProvider? time = null);

public Task RunAsync(TimeSpan interval, Func<SweepReport, Task>? onPass = null, CancellationToken cancellation = default);
```

> `olderThan` is purely an age threshold (the sweep does not probe liveness); set it longer than your
> longest legitimate flow.

## Maintenance runner

The built-in runner drives all three passes on their own cadences. `utility` is a `WorkflowUtility`
(`SoEx.Method.Workflow`) — the consumer-side component that owns the durable stores and the maintenance
passes; its external face is `SoEx.Method.Workflow.External.IWorkflowUtility`:

```csharp
WorkflowMaintenance.RunAsync(utility, new WorkflowMaintenanceOptions { Enabled = true }, cancellationToken);
```

In-process, no leader election. The utility's external face also exposes `SweepAbandonedAsync` /
`ReDriveHeldAsync` / `ReviewDeadlinesAsync` for a dedicated scheduler to call (the production pattern).

## Maintenance state logs

Durable state the passes need; in-memory defaults, with shipped RavenDB and EF Core implementations.

| Log | Written by | Holds |
|---|---|---|
| `IHeldInstanceRegistry` | the termination | instances quarantined at termination |
| `IErasureRequestRegistry` | `EraseAsync` | open erasure requests (subjects stored only as a one-way `ISubjectProtector` token, never recoverable plaintext at rest) |
| `IPendingErasureRequests` | `RequestEraseAsync` | admitted-but-not-yet-drained requests (the async front door's durable intake) |

## Multiple managers on one utility

When several managers (distinct entrypoints) share one `WorkflowUtility`, each owns its own
`IErasureEvents`, so the utility cannot drive every instance through a single contract. Supply the utility's
`resolveErasureFor` with a per-instance router; `ErasureRouting` (`SoEx.Method.Workflow`) builds one from the
instance-id prefix:

```csharp
Func<string, IErasureEvents?> routing = ErasureRouting.ByPrefix(new Dictionary<string, IErasureEvents>
{
    ["onboarding"] = onboardingErasure,   // instance ids minted as "onboarding-…"
    ["billing"]    = billingErasure,       // "billing-…"
});
var utility = new WorkflowUtility(seam, keys, index, resolveErasureFor: routing);
```

`PrefixOf(instanceId)` reads the flow prefix off an id (`DeterministicInstanceId` mints `{prefix}-{hex}`); an
unknown prefix routes to `null`, which the coordinator surfaces as a not-erased outcome rather than shredding
against the wrong contract. Left unset, the utility keeps single-manager behaviour (the one framework proxy).
The shred stays synchronous and per-manager; see the worked
[multi-manager example](../../examples/MultiManager).

> **In production the routing map must cover every deployed flow.** An instance whose prefix is not mapped is
> reported as unresolved and **left un-erased** — its key is kept — so a missing entry silently means a subject
> is not forgotten. Keep the map in step with the managers you deploy, and monitor for unresolved outcomes.
> Namespace each manager's flow prefixes (for example `membership.onboard`, `billing.invoice`) so two managers
> cannot mint the same instance id into the shared stores; a genuine collision is caught by the per-instance
> AAD (a decrypt failure) rather than silently cross-decrypting, but it should be prevented by construction.

## Right-to-erasure: admit and drain

Erasure is a durable admit-and-drain pair on the external face — there is no synchronous variant:

| Member | Description |
|---|---|
| `Task<string> RequestEraseAsync(string subject)` | Resolve the subject to its instances from the index now, admit those PII-free instance ids to the `IPendingErasureRequests` store under a PII-free request id, and return at once without shredding. Idempotent on the subject. |
| `Task<int> DrainEraseRequestsAsync()` | One pass: drive each admitted instance to crypto-shred through its owning manager's termination — a synchronous, per-manager shred. One of the maintenance passes (the built-in runner drives it by default); the host owns the cadence. |

`IPendingErasureRequests.Backlog()` returns a `PendingBacklog(int Count, DateTimeOffset? OldestReceivedAt)` —
the admitted-but-undrained depth and the oldest admit time (`null` when empty). It is a monitoring read, not the
hot admit path: age the oldest against your statutory window to alert before an unscheduled or stalled drain
breaches a deadline.

The async boundary is the request intake only; the shred itself stays the synchronous call described in
[Why the sequence runs synchronously](../explanation/crypto-shred-and-erasure.md). The admit stores PII-free
instance ids (not the subject), so a durable pending store holds no recoverable subject at rest. A request
lost before the drain only delays the start of erasure, which the sweep and deadline review already backstop;
an instance started after the admit is likewise caught by those backstops. A durable `IPendingErasureRequests`
makes the admit survive a crash before the drain (accept-before-acknowledge) — and it is the *same* durable
store as the erasure-request registry: `RavenDbErasureRequestRegistry` and `EfCoreErasureRequestRegistry`
implement both interfaces, so one connection serves the open-request registry and the pending intake.

The durable `IErasureRequestRegistry` implementations require the same `ISubjectProtector` the durable
[subject index](../how-to/make-crypto-shred-durable.md) uses: a request's subjects are persisted only as the
protector's one-way token, so no recoverable subject of a person exercising erasure sits at rest (deadline
review routes by instance id and never needs the plaintext back).
