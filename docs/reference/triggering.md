> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Reference: triggering

The triggering types start a flow and send events to it. The caller needs only the business identity.
Namespace: `SoEx.Workflow`. For the procedures, see
[Trigger flows from outside](../how-to/trigger-flows-from-outside.md). For the behavior of each runtime,
see the [gateway-semantics matrix](runtime-matrix.md#gateway-semantics).

## `DeterministicInstanceId`

`DeterministicInstanceId` derives a PII-free instance id from the business identity. It keeps no state.
The start side and the continue side derive the same id from the same identity.

```csharp
public static string For(string prefix, params string[] parts);                            // unsalted SHA-256, 128-bit hex
public static string Keyed(ReadOnlySpan<byte> secret, string prefix, params string[] parts); // HMAC-SHA256 under a shared secret
```

| Member | Property | Use when |
|---|---|---|
| `For` | Not secret, and confirmable. A person who has the identity can derive the id again. | The start and continue sides have no shared secret. |
| `Keyed` | A person without the secret cannot derive or confirm the id. | A person who knows the identity must not be able to guess the id. |

## `WorkflowSealer`

`WorkflowSealer` does the seal operation only. Use it in code that responds to a trigger and cannot hold
the dispatch endpoint.

```csharp
public WorkflowSealer(IInstanceKeyStore keys, IMessageSerializer serializer, string operationName,
    IErasureTombstone? tombstone = null, Type? contract = null);
public byte[] Seal(string instanceId, object stepDto, byte[]? ambientContext = null);
public byte[] SealEventData(string instanceId, object eventData);
```

- `Seal` seals a step DTO. Use it for a seed, or for the next step of a raise at a branch that declares
  no `OnEvent`.
- `SealEventData` seals event data for a raise at a branch that declares an `OnEvent`. It needs the
  entrypoint contract. It checks that the operation declares a second parameter of the type of the data.
- If a `tombstone` is wired and the instance was erased, both methods refuse to mint the key.

## `IWorkflowGateway`

`IWorkflowGateway` is the client seam. Each adapter implements it.

```csharp
public interface IWorkflowGateway
{
    Task StartAsync(string instanceId, byte[] sealedSeed);
    Task RaiseEventAsync(string instanceId, string eventName, byte[]? sealedPayload = null, string? raiseId = null);
}
```

- `StartAsync` starts a new instance.
- `RaiseEventAsync` raises a named event at a running instance. A portable wait resumes into the `OnEvent`
  step of the branch. If the raise carries data, the step receives it as event data. If the branch
  declares no `OnEvent`, the raised payload becomes the next step.
- A stable `raiseId` makes one specific raise idempotent. This behavior is different for each runtime.
  See the matrix.

| Runtime | Gateway | Notes |
|---|---|---|
| InProc | `InProcWorkflowGateway<I>` | Owns the instance registry. `CompletionAsync(id)` returns the results. |
| Durable Task | `DurableTaskWorkflowGateway` | Uses the portable flow by default. For a native orchestration, give its name and an input factory. |
| Temporal | `TemporalWorkflowGateway` | Uses a client and a task queue. Native workflows expose the same `RaiseEvent(name, payload)` signal. |
| Elsa | `ElsaWorkflowGateway` | Starts a definition with the instance id as the correlation id. Resumes the parked bookmark by correlation. |
| Restate | `RestateWorkflowGateway` | Uses the ingress HTTP API. Works across the language boundary into the Restate sidecar. |
| Camunda 8 / Zeebe | `ZeebeWorkflowGateway` | Native flow only. `StartAsync` creates a BPMN process instance. `StartByMessageAsync` removes a duplicate start by message id within a TTL. `RaiseEventAsync` publishes a correlated message. |

## `IGatewayAuthorizer`

`IGatewayAuthorizer` is optional. Each gateway calls it before a start or a raise. If it throws, the
gateway rejects the operation. If no authorizer is wired, the gateway permits all operations.

```csharp
public interface IGatewayAuthorizer
{
    Task AuthorizeStartAsync(string instanceId);
    Task AuthorizeRaiseEventAsync(string instanceId, string eventName);
}
```

See [Authorize the gateway seam](../how-to/authorize-the-gateway-seam.md).

## `GatewaySealGuard`

`GatewaySealGuard` is optional. Each gateway calls it after authorization and before it gives anything
to the runtime. Wire it on each gateway that a network can reach. If no guard is wired, the gateway
sends the bytes to the runtime with no check.

```csharp
public GatewaySealGuard(IMessageSerializer serializer, ISubjectIndex index, ISubjectMatcher? matcher = null, Type? contract = null);
public void GuardRaiseId(string instanceId, string? raiseId);
public void RequireSealed(byte[]? bytes, string what);
```

- `RequireSealed` rejects a seed or a raise payload that deserializes as a plaintext workflow envelope.
  It is a shape check. The seal stays the boundary.
- `GuardRaiseId` rejects a raise id that contains a subject that the index knows for the instance. The
  runtime journals the raise id in clear text.

## `SubSystem.IWorkflowUtility`

`SubSystem.IWorkflowUtility` is the face that a Manager uses to drive a flow. A peer component calls it
through a proxy. A host holds the gateway and the sealer. A Manager uses this face only. Namespace:
`SoEx.Method.Workflow.SubSystem`.

```csharp
public interface IWorkflowUtility
{
    Task<StartOutcome> StartAsync(string flowKey, string instanceId, string subject, object firstStep);
    Task RaiseEventAsync(WorkflowEvent raise);
    Task<string[]> SubjectsForAsync(string instanceId);
    Task<string[]> InstancesForAsync(string flowKey, string subject);
}
```

| Member | Use when |
|---|---|
| `StartAsync` | You start a flow. It seals `firstStep` with the key of the instance and binds `subject` as the ambient. It returns `AlreadyExists` as data and does not throw. A duplicate start from a derived id is the usual case, and the answer must go back through the proxy. |
| `RaiseEventAsync` | You continue a parked flow with a business event. The flow resumes into the continuation that it sealed at wait time. The caller needs no knowledge of the flow. A `WorkflowEvent` names the flow, the instance, and the event. Its optional parts are described in [`WorkflowEvent`](#workflowevent). |
| `SubjectsForAsync` | You need the subjects that the index still maps to an instance. The main use is a must-retain carve-out in `OnRetaining`. It opens a blob sealed with the key of the instance. Thus, after the shred of the instance, it returns no subjects. |
| `InstancesForAsync` | You need the instances of this flow that hold a subject. This is the reverse of `SubjectsForAsync`. |

### `WorkflowEvent`

`WorkflowEvent` is the raise that a Manager sends. The constructor takes the parts that every raise needs.
The optional parts are init properties. Set only the parts that you need.

```csharp
public sealed record WorkflowEvent(string FlowKey, string InstanceId, string Name)
{
    public object? Data { get; init; }
    public string? RaiseId { get; init; }
}

await utility.RaiseEventAsync(new WorkflowEvent("onboard", id, "account-verified") { RaiseId = "verified" });
```

| Member | Use when |
|---|---|
| `FlowKey`, `InstanceId`, `Name` | Always. The utility rejects a `Name` that contains a subject that the index knows for the instance, because the runtime journals the name in clear text. |
| `Data` | The raiser knows something that the flow cannot know. The utility seals it with `SealEventData` under the key of the instance. The step operation of the flow must declare a second parameter to receive it, or the seal rejects the raise. On an allow-listed serializer, add the type of the data to `KnownTypes`. |
| `RaiseId` | The same event can arrive more than once, for example from a webhook that retries. A raise that repeats an id that the instance handled returns and does not resume the flow again. Every route that raises the same event must send the same id. The utility rejects an id that contains a subject that the index knows for the instance, because the runtime journals the id in clear text. The behavior is different on each runtime. See the [idempotent-raise row of the matrix](runtime-matrix.md#gateway-semantics). |

A `RaiseId` removes only duplicates of a raise that succeeded. A raise that arrives when the instance does
not wait for the event fails as before.

### When to use `InstancesForAsync`

The primary route to an instance id is derivation. `DeterministicInstanceId` derives the id again from the
business identity. This route needs no store and no lookup. Use `InstancesForAsync` only when derivation
cannot find the instance. That is the case for a subject that the flow found during its run. See
[`WorkflowAction`](workflow-action.md#enrolling-a-subject-the-step-learned). The id comes from the start
subject of the flow. Thus, a derivation from the later subject cannot find it.

These rules apply to the scope of `InstancesForAsync`:

- The scope is one flow key. The index contains all flows and all components that share a utility. Thus,
  a result without a scope would include the instances of a peer.
- The scope uses the `{flowKey}-{hex}` form that `DeterministicInstanceId` mints. If an instance id has a
  different form, the method cannot read its flow and does not return it. This short answer is safe, but
  it gives no error. To include an instance in the results, derive its id with `DeterministicInstanceId`.
- The host must wire the flow. `StartAsync` and `RaiseEventAsync` have the same admission rule.

`InstancesForAsync` continues to answer after the crypto-shred. It matches the one-way lookup token of the
subject and does not open a sealed blob. It answers until the termination prunes the edges.

For the operational face (`RequestEraseAsync` and the maintenance passes), see the
[erasure API](erasure-api.md).
