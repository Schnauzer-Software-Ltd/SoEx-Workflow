# Choose a message serializer

SoEx ships several message serializers, and a governed flow runs on any of them. The stock
`DefaultPipeline` selects System.Text.Json (`JsonMessageSerializer`), which binds every value to its
declared type. It carries no type markers a reader could be fooled by, and in exchange it needs to be told
about a handful of types up front. BoundJson works the same way. The Newtonsoft `OpenJsonMessageSerializer`
writes a type marker beside every value instead, so it needs nothing from you.

## Declare the known types

A binding serializer can bind a value to its declared type wherever one exists. Two places in a governed
flow have none, because the declared type there is `object`:

- The ambient context bag, which carries the subject stop as a dictionary value on every governed step.
- The portable `WorkflowAction`, whose `Complete.Result`, `RaiseIntoNext.NextStep`, `Loop.CarryState`,
  `WaitForEvent.OnTimeout` and `EventBranch.OnEvent` members hold your step DTOs.
- The event-data argument, if your step operation declares one. It rides the same envelope slot rule as the
  step DTO, so a closed-hierarchy event type needs its variants registered just as a step hierarchy does.

The framework half of that list is `WorkflowKnownTypes.Framework`. The other half is your own step DTOs,
which the framework cannot know. Where a step DTO is a closed hierarchy — one base declared on the
operation, a variant per step kind — register the concrete variants, since it is the variant that travels:

```csharp
var knownTypes = new KnownTypes([
    .. WorkflowKnownTypes.Framework,
    typeof(OnboardStep.LookupUser),
    typeof(OnboardStep.AssignSubscription),
    typeof(OnboardStep.Abandon),
]);

builder.SoEx(topology, knownTypes);
```

A type the writing host has not declared fails on the first step that needs it, and the message names the
type it wanted. Reading is not symmetric: a host that meets an ambient-context entry whose type it has not
declared drops that entry and carries on, so a subject stop can go missing without an error. Declare the
same types on every host that takes part in a flow, including a worker you deploy separately from the
caller.

## Select a different serializer

A pipeline names the serializer, so selecting another one means setting that one property on
`DefaultPipeline`:

```csharp
using SoEx.Hosting.Default;
using SoEx.Hosting.Serializers.NewtonsoftJson;
using SoEx.Topology.Pipeline;

var pipeline = new DefaultPipeline { MessageSerializer = new PipelineSerializer<OpenJsonMessageSerializer>() };
```

Pass it where you already pass the topology — `builder.SoEx(topology, knownTypes, pipeline)` — or set it
as `Defaults` on a `Topology.System`. On the open serializer the known types are not needed; on BoundJson
they are, exactly as above.

## Name the contract when you seal outside the governed step

`GovernedStep<I>` knows the entrypoint contract and reads and writes every envelope against it. A
`WorkflowSealer` you build yourself does not, so tell it:

```csharp
var sealer = new WorkflowSealer(keys, serializer, nameof(IOnboardSteps.Step), contract: typeof(IOnboardSteps));
```

The endpoint reads a step envelope against the contract, so the seal has to write it the same way. On the
open serializer the argument is ignored and omitting it costs nothing, which is why it is optional; on a
binding serializer, the stock one included, omitting it leaves the two halves disagreeing about how the
step DTO on the wire is named. A concrete DTO survives that disagreement and a closed hierarchy does not, so the failure shows up
against exactly the flows most likely to be in production.

`GatewaySealGuard` takes the same optional argument and rarely wants it: a gateway usually fronts several
flows, and the guard only needs to see whether bytes parse as an envelope at all.

## What this buys

Arguments and results cross the wire as their declared types. Nothing on the wire tells the reader what
to construct, so nothing on the wire can talk it into constructing something else — the reader already
knows what it expects. The seal is still the security boundary either way: the framework only ever hands
a serializer bytes it decrypted itself.

## See also

- [The governed core](../reference/governed-core.md) — the wiring sequence this plugs into.
- [`WorkflowAction`](../reference/workflow-action.md) — the portable model's object-typed members.
