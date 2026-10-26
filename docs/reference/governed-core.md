> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Reference: the governed core

The governed core is the set of `SoEx.Workflow` types that both consumption models use. The types are in
three namespaces:

- `SoEx.Workflow`: the core types.
- `SoEx.Workflow.Runtime.InMemory`: the in-process implementations.
- `SoEx.Transport.Workflow`: the workflow binding, transport, channel, endpoint, and `WorkflowListeners`.
  The `SoEx.Transport.Workflow` package contains this namespace. It is one of the `SoEx.Transport.*`
  transports.

## `GovernedStep<I>`

`GovernedStep<I>` dispatches your step component one time through the SoEx pipeline. The path is: endpoint
pipeline → `DefaultDispatcher` → `component.<op>(typedDto)`. For each step, `GovernedStep<I>` does these
operations:

- It mints the per-instance key.
- It indexes the subject.
- If you wire an idempotency store, it applies each step effect one time for each
  `(InstanceId, DtoType, Sequence)` triple. An at-least-once redelivery then has no second effect.
- If you wire a lease registry, it renews the lease of the instance after the step completes.

```csharp
public GovernedStep<I>(
    IWorkflowDispatch endpoint,
    IMessageSerializer serializer,
    IIdempotencyStore? idempotency,
    IInstanceKeyStore keys,
    ISubjectIndex index,
    string? operationName = null,
    ISubjectMatcher? subjectMatcher = null,
    bool clearJournalResult = false,
    WorkflowMetrics? metrics = null,
    IErasureTombstone? tombstone = null,
    IInstanceLeaseRegistry? leases = null,
    InstanceLeaseOptions? leaseOptions = null,
    TimeProvider? time = null) where I : class;
```

| Member | Description |
|---|---|
| `Task<T> ExecuteAsync<T>(StepContext context, object stepDto, byte[]? sealedEventData = null)` | Dispatches one governed step and returns the typed result `T` of the component. A native flow that received an event gives the sealed data here. The second parameter of the step operation receives that data. |
| `byte[] SealStep(string instanceId, object stepDto, byte[]? ambientContext = null)` | Mints the key on first use and seals a step DTO with it. Returns the sealed seed or payload. |
| `byte[] SealEventData(string instanceId, object eventData)` | Mints the key and seals raise-time event data with it. A raiser uses this form to give data to the continuation that the flow declared. The method refuses the call if the step operation has no parameter for event data. |
| `bool AcceptsEventData` | Is `true` if the step operation declares the second parameter. |
| `T UnsealStep<T>(string instanceId, byte[] sealed)` | Decrypts a sealed payload to a typed DTO. The key must be live. |
| `byte[] AmbientOf(string instanceId, byte[] sealed)` | Gets the ambient bytes from a sealed payload. Throws `InvalidOperationException` after the crypto-shred of the key. |
| `byte[]? EnrollSubjects(string instanceId, byte[]? ambient, WorkflowAction action)` | Adds the subjects that an action declared to the subject context of the step. It indexes them immediately. It returns the ambient bytes that seal each continuation after this step. Portable drivers call this method after the step returns and before they guard or flatten the action. See [`WorkflowAction`](workflow-action.md#enrolling-a-subject-the-step-learned). |
| `IMessageSerializer Serializer` | The serializer of this step. |

`operationName` selects the step operation when the contract has more than one operation. For a contract
with one operation, omit it. `subjectMatcher` replaces the default clear-text guard. See
[Customize PII detection](../how-to/customize-pii-detection.md).

`leases` and `leaseOptions` set the lease that each completed step gives its instance. `leaseOptions`
defaults to `InstanceLeaseOptions.Default`: `Idle` is 1 day and `WaitMargin` is 1 hour. `time` is the clock
for the lease. See [Give each instance a lease](../how-to/run-erasure-maintenance.md#give-each-instance-a-lease).

A step operation has one or two parameters. The first parameter is the step DTO. The optional second
parameter is the event data that a raise can carry. The framework makes the argument array, and it has
values for these two slots only. Thus, if a step operation has a different number of parameters, the
constructor of `GovernedStep` rejects it. This check occurs when you build the `GovernedStep`, before the
first step of a live instance.

`GovernedStep<I>` also has a non-generic facet, `IGovernedStep`. This facet contains these members:

- the guards for the instance id, the result, and the visible names
- `SealStep`
- `SealEventData`
- `AmbientOf`
- `EnrollSubjects`
- `Serializer`

The shipped host builders accept `IGovernedStep`. Thus, a host that drives many entrypoints can keep all
of them as one type.

### Scope of the guard

The default matcher finds a known subject id when the id occurs literally as a substring. It examines
runtime-visible names and serialized results. The scan on the byte path ignores ASCII case, so it also
finds an id in a different case. These limits apply to the default matcher:

- If a serializer escapes characters as `\uXXXX`, the literal byte match fails.
- The matcher finds known subjects only. It does not find an unknown PII value or a value derived from PII.

The default matcher is a safety net for known subjects. If you need stricter detection, give a stricter
`subjectMatcher`, for example a regex, NER, or a denylist. For each adapter, the
[runtime matrix](runtime-matrix.md) lists the clear-text values that the guard examines. These values
include the instance id, the workflow result, and the event names of a portable wait. A timer has a
duration and no name, so the guard has no timer value to examine. See
[what is sealed vs guarded](../explanation/crypto-shred-and-erasure.md#what-is-sealed-vs-guarded) for the
full list.

## `GovernedTermination`

`GovernedTermination` runs the termination lifecycle for erasure. The sequence is:

1. `OnRetaining`.
2. Remove the lease of the instance, if you wired a lease registry.
3. Destroy the key (crypto-shred).
4. Prune the subject index.
5. `OnTerminated`.

If the extraction fails, the lifecycle goes to `OnRetentionHeld` after `OnRetaining`.

```csharp
public GovernedTermination(
    IErasureEvent? contracts,
    IInstanceKeyStore keys,
    ISubjectIndex index,
    IHeldInstanceRegistry? heldRegistry = null,
    WorkflowMetrics? metrics = null,
    IErasureTombstone? tombstone = null,
    IInstanceLeaseRegistry? leases = null);

public Task<TerminationOutcome> TerminateAsync(string instanceId, IdempotencyKey idempotencyKey, TerminationTrigger trigger);
```

Give `contracts` as a SoEx client of `IErasureEvent`. Resolve the client from the host. The client sends
each erasure event into the host of your component as a request. In the request, an erasure event can
call the components of your component through their proxies. The [wiring sequence](#the-wiring-sequence)
shows the client.

`TerminationTrigger` identifies the cause of the termination: a natural completion or a forced erasure.
`TerminationOutcome` has two values:

- `Terminated`: the framework destroyed the key and pruned the index.
- `Held`: the retention extraction failed after the retry limit, or a step failed and the framework
  parked the instance. The framework keeps the key. A retention hold waits for an audited re-drive. A
  parked instance waits for an intentional termination.

## `StepContext`

`StepContext` gives the durable identity of the step to `ExecuteAsync`.

```csharp
public readonly record struct StepContext(string InstanceId, long Sequence, byte[]? AmbientContext = null)
{
    public TimeSpan? Lease { get; init; }
}
```

`InstanceId` and `Sequence` come from the context of the runtime. `(InstanceId, Sequence)` is also part of
the idempotency triple. Thus, a redelivered step applies its effect one time.

`Lease` is the idle time that the instance gets after this step completes. If it is `null`, the step gets
the `Idle` value of the binding. The framework uses it only when you wire a lease registry. It must be
more than zero.

## `StepMetadata`

`StepMetadata` contains the step facts that the framework uses. The framework reads them from the envelope
and does not interpret your payload. The facts are `InstanceId`, `Sequence`, `DtoType`, `SubjectIds`,
`WorkflowManaged`, and the `IdempotencyKey` triple.

## Hosting types

| Type | Description |
|---|---|
| `WorkflowBinding<I>(string name)` | A standard SoEx binding that hosts your step component. Put it in your topology. |
| `WorkflowListeners` | Collects the endpoints when the host starts. `ForAddress(binding.Transport.Address.Uri)` returns the bound `IWorkflowDispatch`. |
| `WorkflowRegistration.RequireErasureEvent(Type)` | Throws at wiring time if the component does not implement `IErasureEvent`. |
| `WorkflowEnvelope.AmbientFor(IMessageSerializer, SubjectContext?)` | Makes the ambient bytes that carry a subject. Returns `byte[]?`. |
| `WorkflowKnownTypes.Framework` | The framework types that you declare to the serializer of the host, together with your step DTOs. See [choose a serializer](../how-to/choose-a-serializer.md). |

## The wiring sequence

The wiring sequence has five parts. Do them in this order:

1. Host the component. Bind it to `WorkflowBinding<I>` and to `InProcBinding<IErasureEvent>`, and add a
   client of `IErasureEvent` to the system.
2. Start the host.
3. Resolve the endpoint and the `IErasureEvent` client.
4. Build the `GovernedStep`.
5. Build the `GovernedTermination` on the client.

```csharp
IInstanceKeyStore keys  = new InMemoryInstanceKeyStore();
ISubjectIndex     index = new InMemorySubjectIndex();
IIdempotencyStore idem  = new InMemoryIdempotencyStore();
IInstanceLeaseRegistry leases = new InMemoryInstanceLeaseRegistry();   // optional: share it with the sweep

var listeners = new WorkflowListeners();
var binding   = new WorkflowBinding<IOnboardManager>("onboarding");   // the governed steps arrive here
var erasure   = new InProcBinding<IErasureEvent>("onboarding");      // the erasure events arrive here
var services  = new ServiceCollection();
services.AddSingleton(listeners);
services.AddSingleton<IContextFlowPolicy, SubjectContextFlowPolicy>();

var topology = new SoEx.Topology.System   // qualified — `Host` below is Microsoft's host builder
{
    Defaults = new DefaultPipeline(),
    SubSystems =
    [
        new SoEx.Topology.SubSystem
        {
            Name = "onboarding",
            EntryPoint = new SoEx.Topology.Host
            {
                Implementation = typeof(OnboardManager),
                Endpoints = [binding, erasure], Proxies = [], ServiceCollection = services,
            },
            Components = [],
        },
    ],
    Clients = [new SoEx.Topology.Client<IErasureEvent> { SubSystem = "onboarding", Service = erasure }],
};
var knownTypes = new KnownTypes([.. WorkflowKnownTypes.Framework /* , your step DTOs */]);
var builder = Host.CreateApplicationBuilder();
InProcExtensions.InProcClientWithSpan(builder.Services, services);
builder.SoEx(topology, knownTypes);
IHost host = builder.Build();
host.Start();                                                          // endpoint registers as the host starts

IWorkflowDispatch endpoint = listeners.ForAddress(binding.Transport.Address.Uri);   // resolve AFTER Start
var serializer = host.Services.GetRequiredService<IMessageSerializer>();
IErasureEvent erasureEvents = host.Services.GetRequiredService<IErasureEvent>();   // the SoEx client

WorkflowRegistration.RequireErasureEvent(typeof(OnboardManager));
var step        = new GovernedStep<IOnboardManager>(endpoint, serializer, idem, keys, index,
                                                    leases: leases);           // optional: instance leases
var termination = new GovernedTermination(erasureEvents, keys, index, leases: leases);
```

Resolve the endpoint only after `host.Start()`. The snippet is the full composition, and it includes the
guard. The private test suite puts this composition into a helper that returns `(step, termination)`.

The snippet sets `Defaults` to the stock `DefaultPipeline`. Its serializer is System.Text.Json. For this
reason, the snippet gives the known types. To use a different serializer, set `Defaults` to a pipeline
with that serializer. See [choose a message serializer](../how-to/choose-a-serializer.md).

## Related pages

- [Governance services](governance-services.md): `IInstanceKeyStore`, `ISubjectIndex`,
  `IIdempotencyStore`.
- [Erasure events](erasure-events.md): `IErasureEvent` and its context types.
- [`WorkflowAction`](workflow-action.md): the return value of the portable model.
