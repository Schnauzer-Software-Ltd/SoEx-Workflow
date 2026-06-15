> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Reference — triggering

The types for starting and signaling a flow from a caller holding only business identity. Namespace:
`SoEx.Workflow`. For task guidance see [Trigger flows from outside](../how-to/trigger-flows-from-outside.md);
for per-engine behavior see the [gateway-semantics matrix](runtime-matrix.md#gateway-semantics).

## `DeterministicInstanceId`

Derives a PII-free instance id from business identity. Stateless: the start and continue sides derive
the same id from the same identity.

```csharp
public static string For(string prefix, params string[] parts);                            // unsalted SHA-256, 128-bit hex
public static string Keyed(ReadOnlySpan<byte> secret, string prefix, params string[] parts); // HMAC-SHA256 under a shared secret
```

| Member | Property | Use when |
|---|---|---|
| `For` | non-secret, confirmable (a holder of the identity can re-derive it) | the start/continue sides have no shared secret |
| `Keyed` | not derivable or confirmable without the secret | the id must be unguessable by someone who knows the identity |

## `WorkflowSealer`

The seal side alone, for code that reacts to a trigger but can't hold the dispatch endpoint.

```csharp
public WorkflowSealer(IInstanceKeyStore keys, IMessageSerializer serializer, string operationName);
public byte[] Seal(string instanceId, object stepDto, byte[]? ambientContext = null);
```

## `IWorkflowGateway`

The client seam every adapter implements.

```csharp
public interface IWorkflowGateway
{
    Task StartAsync(string instanceId, byte[] sealedSeed);
    Task RaiseEventAsync(string instanceId, string eventName, byte[]? sealedPayload = null, string? raiseId = null);
}
```

- `StartAsync` submits a new instance.
- `RaiseEventAsync` raises a named event at a running one. An omitted payload resumes a portable wait
  into its `OnEvent` step. A stable `raiseId` makes a specific raise idempotent (per-engine — see the
  matrix).

| Runtime | Gateway | Notes |
|---|---|---|
| InProc | `InProcWorkflowGateway<I>` | owns the instance registry; `CompletionAsync(id)` observes results |
| Durable Task | `DurableTaskWorkflowGateway` | portable flow by default; a native orchestration via its name + an input factory |
| Temporal | `TemporalWorkflowGateway` | client + task queue; native workflows expose the same `RaiseEvent(name, payload)` signal |
| Elsa | `ElsaWorkflowGateway` | starts a definition with the instance id as the correlation id; resumes the parked bookmark by correlation |
| Restate | `RestateWorkflowGateway` | the ingress HTTP API; works across the language boundary into the Restate sidecar |
| Zeebe | `ZeebeWorkflowGateway` | native-only; `StartAsync` creates a BPMN process instance; `StartByMessageAsync` dedupes a duplicate start by message id within a TTL; `RaiseEventAsync` publishes a correlated message |

## `IGatewayAuthorizer`

Optional. Every gateway consults it before a start or raise; a throw rejects the operation. With none
wired, the gateway allows everything.

```csharp
public interface IGatewayAuthorizer
{
    Task AuthorizeStartAsync(string instanceId);
    Task AuthorizeRaiseEventAsync(string instanceId, string eventName);
}
```

See [Authorize the gateway seam](../how-to/authorize-the-gateway-seam.md).
