> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# How to trigger flows from outside

This guide shows how an external caller starts a flow and raises events on it. An external caller is
code that runs outside the place where you wired the flow. Two examples are the webhook of an identity
provider ("this account was verified") and a payment processor ("this card was updated").

An external caller holds only business identity, for example an org and an email, or a subscriber id.
It has no instance handle. It has no knowledge of the steps of the flow.

You combine three parts into one small operation on your entrypoint:

1. A deterministic instance id.
2. A sealer for the seed.
3. A gateway.

## 1. Derive the instance id from business identity

The runtime journal keeps instance ids in clear text. Thus an instance id must contain no PII.
`DeterministicInstanceId` derives an instance id with no PII from business identity. It keeps no state.
The code that starts the flow and the webhook that continues it months later derive the same id from the
same identity. They do no lookup.

1. Call `DeterministicInstanceId.For` with a flow prefix and the business identity.

```csharp
string instanceId = DeterministicInstanceId.For("onboard", orgId, email);
// e.g. "onboard-3f9a1c0e7b2d48569f0a1c0e7b2d4856" — 32 hex chars, PII-free by construction
// (the suffix folds in the "onboard" prefix, so the same identity under a different flow gets a different id)
```

> The unkeyed `For` is confirmable. A person who holds a candidate identity can derive the id again. If
> the id must be unguessable, use `DeterministicInstanceId.Keyed(...)`. See
> [Authorize the gateway seam](authorize-the-gateway-seam.md).

## 2. Seal the first step without holding the endpoint

To start a flow, you need a sealed seed. The component that reacts to the trigger usually cannot hold the
dispatch endpoint. `WorkflowSealer` does the seal operation only. It uses a key store, a serializer, and
the name of the step operation.

1. Create a `WorkflowSealer` with the key store, the serializer, and the operation name.
2. Call `Seal` with the instance id, the first step DTO, and the ambient bytes.

```csharp
var sealer = new WorkflowSealer(keys, serializer, nameof(IOnboardManager.Run));
byte[] seed = sealer.Seal(instanceId, new OnboardStep.Lookup(email), ambient);
```

## 3. Start and raise events through one interface

`IWorkflowGateway` is the client seam. Each adapter implements it.

1. Select the gateway for your runtime.
2. Call `StartAsync` with the instance id and the seed to submit a new instance.
3. Call `RaiseEventAsync` with the instance id and an event name to raise an event on a running instance.

```csharp
await gateway.StartAsync(instanceId, seed);                       // submit a new instance
await gateway.RaiseEventAsync(instanceId, "account-verified");    // raise a named event at a running one
```

These are the gateways for each runtime:

- `InProcWorkflowGateway<I>`
- `DurableTaskWorkflowGateway`
- `TemporalWorkflowGateway`
- `ElsaWorkflowGateway`
- `RestateWorkflowGateway`
- `ZeebeWorkflowGateway`

The interface is the same on each runtime. Start idempotency and the behavior of a raise before a wait
are different on each runtime. Before you use an edge behavior, read the
[per-adapter table in the runtime matrix](../reference/runtime-matrix.md#gateway-semantics).

## Raise an event with no payload

A portable wait can set the meaning of a bare event in advance. A bare event is an event with no payload.
The `OnEvent` continuation of an event branch gives this meaning. The framework seals the `OnEvent` step at
wait time and journals it, the same as `OnTimeout`.

1. Give each event branch of `WaitForEvent` an `OnEvent` step.

```csharp
return new WorkflowAction.WaitForEvent(
    [new EventBranch("account-verified", new OnboardStep.Invite(email, reservationId))],  // the bare event arrived
    ttl,
    OnTimeout: new OnboardStep.Release(reservationId));                                    // the timer fired
```

2. Call `gateway.RaiseEventAsync(instanceId, "account-verified")` from the caller.

The wait resumes into the journaled `OnEvent` step. The caller sends no payload. The caller needs no
knowledge of the flow and no key material.

If a wait has no `OnEvent`, a bare raise into it fails. The flow gave no meaning to the bare event.

If a branch has an `OnEvent`, that step always runs. It runs for a bare raise and for a raise with data.
The step that the flow chose stays the next step.

To send data with the raise:

1. Seal the data with `SealEventData`.
2. Receive the data as a second parameter on your step operation.

See [Receiving data with an event](../reference/workflow-action.md#receiving-data-with-an-event).

If a branch has no `OnEvent`, the raised payload becomes the next step. Seal that step with `Seal`. In
this case only, the caller must know the flow well enough to write that step.

## Let more than one event resume a wait

A waiting instance can resume on more than one event. Give the wait one branch for each event. Each
branch names its event and the step that a bare raise of that name starts. All branches race the timer.

1. Add one `EventBranch` for each event name to `WaitForEvent`.

```csharp
return new WorkflowAction.WaitForEvent(
    [
        new EventBranch("verified", new OnboardStep.Provision(orgId, userId)),
        new EventBranch("resend",   new OnboardStep.SendCode(orgId, userId, attempt + 1)),
    ],
    TimeSpan.FromHours(72),
    OnTimeout: new OnboardStep.Abandon("code expired"));
```

2. Order the branches by priority.
3. Raise events from the callers with the same call as before.

The name of the raised event selects the branch. Two callers with different meanings use different event
names. Thus, if a real verification and a resend arrive together, the flow resumes on the correct branch.
The framework loses neither raise.

If two events can be delivered when the wait starts, the first declared branch wins. This is true on
each runtime.

> [!CAUTION]
> Add a `Loop` after a repeatable branch on Restate. On Restate, a durable promise is write-once for
> each event name in each generation. Thus a branch that callers raise many times fires only once. A
> resend button is the usual example. The branch fires again only if the flow takes a `Loop` after it
> handles the branch. See the
> [multi-branch row of the runtime matrix](../reference/runtime-matrix.md#gateway-semantics).

To make one raise idempotent:

1. Pass a stable `raiseId` to the raise.

The behavior is different on each runtime. See the
[idempotent-raise row of the matrix](../reference/runtime-matrix.md#gateway-semantics).

## Put it together

The trigger seam is an ordinary operation on your entrypoint. Callers give only the business identity.

1. Add a start operation that derives the id, seals the seed, and calls `StartAsync`.
2. Add one operation for each event that derives the id and calls `RaiseEventAsync`.

```csharp
public async Task<string> BeginOnboarding(string orgId, string email)
{
    string id = DeterministicInstanceId.For("onboard", orgId, email);
    await gateway.StartAsync(id, sealer.Seal(id, new OnboardStep.Lookup(email), AmbientFor(email)));
    return id;   // PII-free — safe to log, return, correlate
}

public Task AccountVerified(string orgId, string email) =>
    gateway.RaiseEventAsync(DeterministicInstanceId.For("onboard", orgId, email), "account-verified");
```

The `IMembershipManager` in the examples ([`examples/`](../../examples/README.md)) is the full version of
this seam. It runs on all six runtimes as an interactive web control panel.

## Next

- [Authorize the gateway seam](authorize-the-gateway-seam.md): enforce authorization at the gateway and
  make instance ids unguessable.
- [Triggering reference](../reference/triggering.md): the signatures.
- [The triggering seam](../explanation/the-triggering-seam.md): the design and its guarantees.
