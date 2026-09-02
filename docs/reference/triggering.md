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

## `SubSystem.IWorkflowUtility`

What a peer component proxies to when it wants to drive a flow. The gateway and sealer above are the
seam a *host* holds; this is the face a Manager sees, and the only one it should. Namespace:
`SoEx.Method.Workflow.SubSystem`.

```csharp
public interface IWorkflowUtility
{
    Task<StartOutcome> StartAsync(string flowKey, string instanceId, string subject, object firstStep);
    Task RaiseEventAsync(string flowKey, string instanceId, string eventName);
    Task<string[]> SubjectsForAsync(string instanceId);
    Task<string[]> InstancesForAsync(string flowKey, string subject);
}
```

| Member | Use when |
|---|---|
| `StartAsync` | Start a flow. Seals `firstStep` under the instance's key, binding `subject` as the ambient. Returns `AlreadyExists` as data rather than throwing, because a re-derived duplicate start is the common case and the answer has to survive the proxy hop. |
| `RaiseEventAsync` | Continue a parked flow with a bare business event. No payload: the flow resumes into the continuation it sealed at wait time, so the caller needs no flow knowledge. |
| `SubjectsForAsync` | Recover the subjects still mapped to an instance — a must-retain carve-out in `OnRetaining` is the case it exists for. Goes quiet once the instance is shredded, because it opens a blob sealed under that instance's key. |
| `InstancesForAsync` | The reverse: which of this flow's instances a subject is in. |

Reach for `InstancesForAsync` only when derivation cannot answer. An instance id is normally
re-derived from business identity with `DeterministicInstanceId` — no store, no lookup — and that stays
the primary route. What derivation cannot reach is a subject the flow *learned* while it ran
(see [`WorkflowAction`](workflow-action.md#enrolling-a-subject-the-step-learned)): the id was derived
from whoever the flow started with, so nothing derived from the person it met later points at it.

Three things about the scoping are worth knowing before you rely on it:

- It is scoped to one flow key deliberately. The index spans every flow and every component sharing a
  utility, so an unscoped answer would name instances belonging to a peer.
- The scope reads the `{flowKey}-{hex}` shape `DeterministicInstanceId` mints. An instance started
  under an id minted by some other convention carries no readable flow and is **not returned** — a short
  answer is the safe failure, but it is a silent one, so derive your ids if you want this to see them.
- The flow must be wired on the host, the same admission `StartAsync` and `RaiseEventAsync` make.

Unlike `SubjectsForAsync`, this does not decay at the crypto-shred: it matches the subject's one-way
lookup token rather than opening a sealed blob, so it answers until the edges are pruned at termination.

For the operational face (`RequestEraseAsync` and the maintenance passes), see the
[erasure API](erasure-api.md).

