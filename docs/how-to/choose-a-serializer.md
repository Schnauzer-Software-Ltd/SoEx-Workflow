> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Choose a message serializer

SoEx has several message serializers. A governed flow runs on each of them. There are two kinds of
serializer:

- A binding serializer binds each value to its declared type. It writes no type markers, so a reader
  cannot be misled by a marker. You must declare some types at startup. The stock `DefaultPipeline`
  selects System.Text.Json (`JsonMessageSerializer`), which is a binding serializer. BoundJson is also a
  binding serializer.
- The open serializer is the Newtonsoft `OpenJsonMessageSerializer`. It writes a type marker beside each
  value. It needs no declared types.

## Declare the known types

A binding serializer binds a value to its declared type. In two places of a governed flow, the declared
type is `object`:

- The ambient context bag. On each governed step, it carries the `SubjectContext` entry as a dictionary
  value. This entry holds the subjects of the instance.
- The portable `WorkflowAction`. Its members `Complete.Result`, `RaiseIntoNext.NextStep`,
  `Loop.CarryState`, `WaitForEvent.OnTimeout` and `EventBranch.OnEvent` hold your step DTOs.

`WorkflowKnownTypes.Framework` holds the framework types for these places: `SubjectContext`,
`EventBranch`, and each `WorkflowAction` variant. Your step DTOs are the other part. The framework cannot
know them, so you declare them.

The event-data argument, if your step operation declares one, uses the same envelope slot rule as the
step DTO. A closed-hierarchy event type needs its variants registered, as a step hierarchy does.

1. Start a `KnownTypes` list with `WorkflowKnownTypes.Framework`.
2. Add each concrete variant of each closed-hierarchy step DTO.

A closed hierarchy has one base type declared on the operation and one variant for each step kind. The
variant is the type that travels on the wire.

3. Pass the known types to `builder.SoEx`.

```csharp
var knownTypes = new KnownTypes([
    .. WorkflowKnownTypes.Framework,
    typeof(OnboardStep.Lookup),
    typeof(OnboardStep.Invite),
    typeof(OnboardStep.Assign),
    typeof(OnboardStep.Release),
]);

builder.SoEx(topology, knownTypes);
```

4. Declare the same known types on each host that takes part in a flow.

This includes a worker that you deploy separately from the caller.

If the writing host does not declare a type, the first step that needs the type fails. The error message
names the type.

> [!WARNING]
> Declare each known type on each reading host. If a host reads an ambient-context entry of an undeclared
> type, it drops the entry and continues. The `SubjectContext` entry can then go missing with no error.

## Select a different serializer

The pipeline names the serializer. To select a different serializer, set one property on
`DefaultPipeline`.

1. Set `MessageSerializer` on a new `DefaultPipeline`.

```csharp
using SoEx.Hosting.Default;
using SoEx.Hosting.Serializers.NewtonsoftJson;
using SoEx.Topology.Pipeline;

var pipeline = new DefaultPipeline { MessageSerializer = new PipelineSerializer<OpenJsonMessageSerializer>() };
```

2. Pass the pipeline with the topology: `builder.SoEx(topology, knownTypes, pipeline)`.

Alternatively, set the pipeline as `Defaults` on a `Topology.System`.

The open serializer needs no known types. BoundJson needs the known types, as in
[Declare the known types](#declare-the-known-types).

## Name the contract when you seal outside the governed step

`GovernedStep<I>` knows the entrypoint contract. It reads and writes each envelope against that contract.
A `WorkflowSealer` that you build yourself does not know the contract.

1. Pass the contract to the `WorkflowSealer` constructor as `contract`.

```csharp
var sealer = new WorkflowSealer(keys, serializer, nameof(IOnboardManager.Run), contract: typeof(IOnboardManager));
```

The endpoint reads a step envelope against the contract, so the seal must write the envelope the same way.
The open serializer ignores the `contract` argument. For this reason, the argument is optional.

> [!CAUTION]
> Pass `contract` when you use a binding serializer, including the stock one. Without it, the seal and the
> endpoint name the step DTO on the wire differently. A concrete DTO still reads correctly. A closed
> hierarchy fails, and closed hierarchies are the most likely flows in production.

`GatewaySealGuard` takes the same optional `contract` argument. It usually does not need it. A gateway
usually serves several flows, and the guard checks only that the bytes parse as an envelope.

## Declared types on the wire

Arguments and results cross the wire as their declared types. The wire holds no data that tells the
reader which type to construct. The reader constructs the type that it expects. Thus the data on the wire
cannot make the reader construct a different type. With each serializer, the seal is the security
boundary. The framework gives a serializer only bytes that the framework decrypted itself.

## See also

- [The governed core](../reference/governed-core.md) gives the wiring sequence for this configuration.
- [`WorkflowAction`](../reference/workflow-action.md) describes the members of type `object` in the
  portable model.
