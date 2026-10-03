> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Reference: erasure API

The erasure API contains the types that run a "forget subject S" request. It also contains the
maintenance passes that close gaps over time. Namespace: `SoEx.Workflow`. For the procedures, see
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
    IErasureRequestRegistry? requestRegistry = null,
    WorkflowMetrics? metrics = null);
```

| Member | Description |
|---|---|
| `Task<ErasureResult> EraseAsync(ErasureRequest request, Func<string, ErasureTarget?> resolve)` | Runs a request from start to end. It finds the instances in the index and makes a decision for each instance. It drives each termination to crypto-shred or to quarantine. Then it makes a report. `resolve` maps an instance id to its `ErasureTarget`. It returns `null` if it cannot resolve the id. |
| `Task<ErasureResult> EraseInstancesAsync(ErasureRequest request, IReadOnlyList<string> instanceIds, Func<string, ErasureTarget?> resolve)` | Erases a set of instance ids that is already resolved. The drain uses it. It makes the same decision for each instance as `EraseAsync`, and it records the open request in the same way. |
| `Task<SweepReport> SweepAsync(TimeSpan olderThan, Func<string, ErasureTarget?> resolve, int? maxInstances = null, CancellationToken cancellationToken = default)` | The backstop that works without a request. It force-terminates each live instance with a key that is older than `olderThan`. `maxInstances` limits the number of aged instances in one pass. The next pass gets the remainder. `null` sets no limit. Requires an `IEnumerableInstanceKeyStore`. |
| `Task<ReDriveReport> ReDriveHeldAsync(Func<string, ErasureTarget?> resolve)` | One pass over the held (quarantined) instances. It tries their extraction again. Requires an `IHeldInstanceRegistry`. |
| `Task<DeadlineReviewReport> ReviewDeadlinesAsync(TimeSpan escalateWithin, Func<string, ErasureTarget?> resolve, Func<DeadlineEscalation, Task>? onEscalate = null)` | One pass over the open requests with a statutory window that closes soon. It force-terminates each instance that would breach the deadline. Requires an `IErasureRequestRegistry` and an `IEnumerableInstanceKeyStore`. |

## Request and result types

```csharp
public sealed record ErasureRequest(string RequestId, DateTimeOffset ReceivedAt, IReadOnlyList<string> Subjects)
{
    public static ErasureRequest For(string requestId, DateTimeOffset receivedAt, params string[] subjects);
}

public sealed record ErasureTarget(
    string InstanceId,
    IErasureEvent Contracts,
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

- `ErasureRequest.ReceivedAt` is the start time of the statutory clock.
- `ErasureAction` has two values:
  - `CompleteNaturally`: the instance is bounded and erases itself before the deadline.
  - `ForceTerminate`.
- `ErasureState` has four values:
  - `Requested`: the coordinator cannot resolve the instance yet. The instance keeps its key for a later pass.
  - `InProgress`.
  - `Complete`: a clean shred.
  - `Held`: the extraction failed, and the instance is in quarantine.
- `DeadlineStatus.EscalateBreachRisk` marks a request that can miss its statutory deadline.

## Collaborators

| Type | Constructor |
|---|---|
| `StatutoryDeadlineClock` | `(IDeadlinePolicy? policy = null, TimeSpan? escalateWithin = null, TimeProvider? time = null)`. If `policy` is null, the clock uses a conservative default window. |
| `ErasurePlanner` | `(TimeProvider? time = null)` |
| `ErasureReporter` | `()` |
| `TerminationCoordinator` | `(IInstanceKeyStore keys, ISubjectIndex index, int maxRetainingAttempts = 3, Func<int, Task>? backoffDelay = null, IHeldInstanceRegistry? heldRegistry = null, ISubjectMatcher? matcher = null, WorkflowMetrics? metrics = null, IErasureTombstone? tombstone = null)` |

## `ErasureSweepLoop`

`ErasureSweepLoop` runs `SweepAsync` on a timer.

```csharp
public ErasureSweepLoop(
    ErasureCoordinator coordinator,
    TimeSpan olderThan,
    Func<string, ErasureTarget?> resolve,
    TimeProvider? time = null);

public Task RunAsync(
    TimeSpan interval,
    Func<SweepReport, Task>? onPass = null,
    Func<Exception, LoopPassHealth, Task>? onError = null,
    int? maxInstancesPerPass = null,
    CancellationToken cancellation = default);
```

> `olderThan` is an age limit only. The sweep does not examine if an instance is live. Set `olderThan` to
> a value that is longer than your longest valid flow.

## Maintenance runner

The built-in runner drives the four passes, each on its own schedule: the drain, the sweep, the re-drive
of held instances, and the deadline review. `utility` is a `WorkflowUtility`
(`SoEx.Method.Workflow`). `WorkflowUtility` is the consumer-side component that owns the durable stores
and the maintenance passes. Its external face is `SoEx.Method.Workflow.External.IWorkflowUtility`:

```csharp
WorkflowMaintenance.RunAsync(utility, new WorkflowMaintenanceOptions { Enabled = true }, cancellationToken);
```

The runner runs in-process and has no leader election. It calls the one-pass operations of the external
face: `DrainEraseRequestsAsync`, `SweepAbandonedAsync`, `ReDriveHeldAsync`, and `ReviewDeadlinesAsync`.
A dedicated scheduler can call these methods. This is the production pattern. Each utility operation
calls the matching `ErasureCoordinator` method. The drain calls `EraseInstancesAsync` for each admitted
request.

## Maintenance state logs

The passes keep durable state in these logs. The defaults are in memory. RavenDB and EF Core
implementations are also shipped.

| Log | Written by | Holds |
|---|---|---|
| `IHeldInstanceRegistry` | the termination | the instances that the termination put in quarantine |
| `IErasureRequestRegistry` | `EraseAsync` | the open erasure requests. Each subject is stored only as a one-way `ISubjectProtector` token. The log keeps no recoverable plaintext at rest. |
| `IPendingErasureRequests` | `RequestEraseAsync` | the admitted requests that the drain has not processed. This is the durable intake of the asynchronous front door. |

## Multiple managers on one utility

Many Managers (different entrypoints) can share one `WorkflowUtility`. Each Manager owns its own
`IErasureEvent`. Thus, the utility cannot drive all instances through one contract. Give the
`resolveErasureFor` parameter of the utility a router for each instance. `ErasureRouting`
(`SoEx.Method.Workflow`) makes a router from the prefix of the instance id:

```csharp
Func<string, IErasureEvent?> routing = ErasureRouting.ByPrefix(new Dictionary<string, IErasureEvent>
{
    ["onboarding"] = onboardingErasure,   // instance ids minted as "onboarding-…"
    ["billing"]    = billingErasure,       // "billing-…"
});
var utility = new WorkflowUtility(seam, keys, index, resolveErasureFor: routing);
```

`PrefixOf(instanceId)` reads the flow prefix of an id. `DeterministicInstanceId` mints ids in the form
`{prefix}-{hex}`. An unknown prefix routes to `null`. The coordinator then reports a not-erased outcome.
It does not shred with the wrong contract. If you do not set the router, the utility uses the
single-Manager behavior (the one framework proxy). The shred is synchronous and occurs for each Manager.
See the [multi-manager example](../../examples/MultiManager).

> [!WARNING]
> **In production, map each deployed flow in the routing map.** If the prefix of an instance is not in
> the map, the coordinator reports the instance as unresolved. The instance stays un-erased and the
> framework keeps its key. Thus, a missing entry causes a subject to stay unforgotten, with no error.
> Update the map when you deploy Managers, and monitor for unresolved outcomes.
>
> Give each Manager a namespace for its flow prefixes, for example `membership.onboard` and
> `billing.invoice`. Then two Managers cannot mint the same instance id into the shared stores. If a
> collision occurs, the per-instance AAD causes a decrypt failure, and no cross-decryption occurs. Design
> your prefixes so that a collision cannot occur.

## Right-to-erasure: admit and drain

Erasure is a durable pair of operations on the external face: admit and drain. There is no synchronous
variant.

| Member | Description |
|---|---|
| `Task<string> RequestEraseAsync(string subject)` | Uses the index to find the instances of the subject immediately. It admits those PII-free instance ids to the `IPendingErasureRequests` store with a PII-free request id. Then it returns immediately. It does not shred. The method is idempotent for each subject. |
| `Task<long> DrainEraseRequestsAsync()` | One pass. It drives each admitted instance to crypto-shred through the termination of the Manager that owns it. The shred is synchronous and occurs for each Manager. The method returns the number of drained instances. It is one of the maintenance passes. The built-in runner drives it by default. The host sets the schedule. |

`IPendingErasureRequests.Backlog()` returns a `PendingBacklog(int Count, DateTimeOffset? OldestReceivedAt)`.
`Count` is the number of admitted requests that are not drained. `OldestReceivedAt` is the oldest admit
time, or `null` when the store is empty. Use `Backlog()` for monitoring. It is not part of the admit path.
Compare the age of the oldest request with your statutory window. Then you can send an alert before an
unscheduled or stopped drain breaches a deadline.

Only the request intake is asynchronous. The shred is the synchronous call that
[Why the sequence runs synchronously](../explanation/crypto-shred-and-erasure.md) describes. These facts
apply to the admit:

- The admit stores PII-free instance ids and does not store the subject. Thus, a durable pending store
  holds no recoverable subject at rest.
- If a request is lost before the drain, the start of erasure is late. The sweep and the deadline review
  are backstops for this case.
- These backstops also find an instance that starts after the admit.
- A durable `IPendingErasureRequests` keeps the admit after a crash before the drain
  (accept-before-acknowledge).
- The durable pending store is the same store as the erasure-request registry.
  `RavenDbErasureRequestRegistry` and `EfCoreErasureRequestRegistry` implement both interfaces. One
  connection serves the open-request registry and the pending intake.

The durable `IErasureRequestRegistry` implementations require the `ISubjectProtector` that the durable
[subject index](../how-to/make-crypto-shred-durable.md) also uses. The registry persists the subjects of a
request only as the one-way token of the protector. Thus, no recoverable subject of a person who requests
erasure is at rest. The deadline review routes by instance id and does not need the plaintext.
