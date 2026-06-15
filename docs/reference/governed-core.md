> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Reference — the governed core

The `SoEx.Workflow` types that both consumption models build on. Namespaces: `SoEx.Workflow` (core),
`SoEx.Workflow.InMemory` (in-process impls).

## `GovernedStep<I>`

Wraps one dispatch of your step component through the SoEx pipeline (endpoint pipeline →
`DefaultDispatcher` → `component.<op>(typedDto)`), minting the per-instance key, indexing the subject,
and, when an idempotency store is wired, collapsing at-least-once redelivery on the
`(InstanceId, DtoType, Sequence)` triple.

```csharp
public GovernedStep<I>(
    IWorkflowDispatch endpoint,
    IMessageSerializer serializer,
    IIdempotencyStore? idempotency,
    IInstanceKeyStore keys,
    ISubjectIndex index,
    string? operationName = null,
    ISubjectMatcher? subjectMatcher = null) where I : class;
```

| Member | Description |
|---|---|
| `Task<T> ExecuteAsync<T>(StepContext context, object stepDto)` | Dispatch one governed step; returns the component's typed result `T`. |
| `byte[] SealStep(string instanceId, object stepDto, byte[]? ambientContext = null)` | Mint the key (on first use) and seal a step DTO under it. Returns the sealed seed/payload. |
| `T UnsealStep<T>(string instanceId, byte[] sealed)` | Decrypt a sealed payload back to a typed DTO (key must be live). |
| `byte[] AmbientOf(string instanceId, byte[] sealed)` | Recover the ambient bytes from a sealed payload (throws `InvalidOperationException` once the key is shredded). |
| `IMessageSerializer Serializer` | The serializer this step was built with. |

`operationName` selects the step operation when the contract has more than one; omit it for a
single-operation contract. `subjectMatcher` overrides the clear-text guard — see
[Customize PII detection](../how-to/customize-pii-detection.md).

`GovernedStep<I>` also exposes a non-generic `IGovernedStep` facet (instance id / result / visible-name
guards, `SealStep`, `AmbientOf`, `Serializer`). This is the type the shipped host builders accept, so a
host that drives several entrypoints can hold them uniformly.

**Guard scope.** The default substring matcher catches a known subject id appearing literally in a
runtime-visible name or a serialized result. It is a safety net rather than general PII detection: the
byte-path scan folds ASCII case (so a re-cased id is still caught), but a serializer that escapes
characters as `\uXXXX` breaks the literal-byte match, and an unknown or derived PII value is not a known
subject. Plug in a stricter `subjectMatcher` (regex/NER/denylist) where that matters; the in-clear surfaces it guards
(instance id, step results, event/timer names) are listed per adapter in the
[runtime matrix](runtime-matrix.md).

## `GovernedTermination`

Runs the termination erasure lifecycle: `OnRetaining` → destroy the key (crypto-shred) → prune the subject
index → `OnTerminated`, or → `OnRetentionHeld` on extraction failure.

```csharp
public GovernedTermination(
    IErasureEvents? contracts,
    IInstanceKeyStore keys,
    ISubjectIndex index,
    IHeldInstanceRegistry? heldRegistry = null);

public Task<TerminationOutcome> TerminateAsync(string instanceId, IdempotencyKey idempotencyKey, TerminationTrigger trigger);
```

`TerminationTrigger` distinguishes a natural completion from a forced erasure. `TerminationOutcome` is
`Terminated` (key destroyed, index pruned) or `Held` (retention extraction failed past the retry
boundary, so the key is retained for an audited re-drive).

## `StepContext`

Carries the durable identity into `ExecuteAsync`.

```csharp
public readonly record struct StepContext(string InstanceId, long Sequence, byte[]? AmbientContext = null);
```

`InstanceId` and `Sequence` come from the backend's own context. The same `(InstanceId, Sequence)` keys
the idempotency triple, so a redelivered step applies its effect once.

## `StepMetadata`

The framework-understood facts of a step, extracted from the envelope without interpreting your payload:
`InstanceId`, `Sequence`, `DtoType`, `SubjectIds`, `WorkflowManaged`, and the `IdempotencyKey` triple.

## Hosting types

| Type | Description |
|---|---|
| `WorkflowBinding<I>(string name)` | An ordinary SoEx binding that hosts your step component; put it in your topology. |
| `WorkflowListeners` | Collects endpoints as the host starts; `ForAddress(binding.Transport.Address)` returns the bound `IWorkflowDispatch`. |
| `WorkflowRegistration.RequireErasureEvents(Type)` | Throws at wiring time if the component doesn't implement `IErasureEvents`. |
| `WorkflowEnvelope.AmbientFor(IMessageSerializer, SubjectContext?)` | Builds the ambient bytes carrying a subject. Returns `byte[]?`. |

## The wiring sequence

Host the component, start the host, resolve the endpoint, then build the two governed types:

```csharp
IInstanceKeyStore keys  = new InMemoryInstanceKeyStore();
ISubjectIndex     index = new InMemorySubjectIndex();
IIdempotencyStore idem  = new InMemoryIdempotencyStore();
var component = new OnboardSteps();

var listeners = new WorkflowListeners();
var binding   = new WorkflowBinding<IOnboardSteps>("onboarding");
var services  = new ServiceCollection();
services.AddSingleton(listeners);
services.AddSingleton<IContextFlowPolicy, SubjectContextFlowPolicy>();

var topology = new SoEx.Topology.HostMock   // qualified — `Host` below is Microsoft's host builder
{
    Instance = component, Implementation = component.GetType(),
    Endpoints = [binding], Proxies = [], ServiceCollection = services,
};
var builder = Host.CreateApplicationBuilder();
builder.SoEx(topology);
IHost host = builder.Build();
host.Start();                                                          // endpoint registers as the host starts

IWorkflowDispatch endpoint = listeners.ForAddress(binding.Transport.Address);   // resolve AFTER Start
var serializer = host.Services.GetRequiredService<IMessageSerializer>();

WorkflowRegistration.RequireErasureEvents(component.GetType());
var step     = new GovernedStep<IOnboardSteps>(endpoint, serializer, idem, keys, index);
var termination = new GovernedTermination(component, keys, index);
```

Resolve the endpoint only after `host.Start()`. The snippet above is the whole composition (guard
included); the private test suite wraps exactly this into a helper that returns `(step, termination)`.

## See also

- [Governance services](governance-services.md) — `IInstanceKeyStore`, `ISubjectIndex`,
  `IIdempotencyStore`.
- [Erasure events](erasure-events.md) — `IErasureEvents` and its context types.
- [`WorkflowAction`](workflow-action.md) — the portable-model return value.
