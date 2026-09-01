> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# How to trigger flows from outside

External triggers don't run where the flow was wired: an identity provider's webhook says "this account
was verified", a payment processor says "this card was updated". Those callers hold only business
identity (an org plus an email, a subscriber id), with no instance handle and no knowledge of the
flow's steps. This guide shows how to start and raise events on a flow from such a caller.

Three pieces compose into one small operation on your entrypoint.

## 1. Derive the instance id from business identity

Instance ids are journaled in clear, so they must be PII-free. `DeterministicInstanceId` derives a
PII-free id from identity, and it's stateless: the code that starts the flow and the webhook that
continues it months later derive the same id from the same identity, with no lookup:

```csharp
string instanceId = DeterministicInstanceId.For("onboard", orgId, email);
// e.g. "onboard-3f9a1c0e7b2d48569f0a1c0e7b2d4856" — 32 hex chars, PII-free by construction
// (the suffix folds in the "onboard" prefix, so the same identity under a different flow gets a different id)
```

> The unkeyed `For` is confirmable: anyone holding a candidate identity can re-derive it. When the id
> must be unguessable, use `DeterministicInstanceId.Keyed(...)` — see
> [Authorize the gateway seam](authorize-the-gateway-seam.md).

## 2. Seal the first step without holding the endpoint

Starting a flow needs a sealed seed, but the component reacting to the trigger usually can't hold the
dispatch endpoint. `WorkflowSealer` is the seal side alone (key store + serializer + the operation
name):

```csharp
var sealer = new WorkflowSealer(keys, serializer, nameof(IOnboardSteps.Run));
byte[] seed = sealer.Seal(instanceId, new OnboardStep.Lookup(email), ambient);
```

## 3. Start and raise events through one interface

`IWorkflowGateway` is the client seam every adapter implements:

```csharp
await gateway.StartAsync(instanceId, seed);                       // submit a new instance
await gateway.RaiseEventAsync(instanceId, "account-verified");    // raise a named event at a running one
```

Pick the gateway for your runtime: `InProcWorkflowGateway<I>`, `DurableTaskWorkflowGateway`,
`TemporalWorkflowGateway`, `ElsaWorkflowGateway`, `RestateWorkflowGateway`, or `ZeebeWorkflowGateway`.
The interface is uniform, but start-idempotency and raise-before-wait semantics differ per engine.
Check the [per-adapter table in the runtime matrix](../reference/runtime-matrix.md#gateway-semantics)
before you rely on an edge behavior.

## Raise an event with no payload

A portable wait can pre-decide what a bare event means by giving `WaitForEvent` an `OnEvent`
continuation, sealed at wait time and journaled just like `OnTimeout`:

```csharp
return new WorkflowAction.WaitForEvent(
    [new EventBranch("account-verified", new OnboardStep.Invite(email, reservationId))],  // the bare event arrived
    ttl,
    OnTimeout: new OnboardStep.Release(reservationId));                                    // the timer fired
```

Now `gateway.RaiseEventAsync(instanceId, "account-verified")` resumes the wait into the journaled
`OnEvent` step, with no payload, no flow knowledge, and no key material on the caller's side. An event
raised with a sealed payload still wins and becomes the next step. A bare raise into a wait with no
`OnEvent` fails, because the flow declared no meaning for it.

## Let more than one event resume a wait

Give the wait a branch per event when a parked instance can be resumed by more than one thing. Each
branch names its event and the step a bare raise of that name means, and all of them race the timer:

```csharp
return new WorkflowAction.WaitForEvent(
    [
        new EventBranch("verified", new OnboardStep.Provision(orgId, userId)),
        new EventBranch("resend",   new OnboardStep.SendCode(orgId, userId, attempt + 1)),
    ],
    TimeSpan.FromHours(72),
    OnTimeout: new OnboardStep.Abandon("code expired"));
```

Callers raise as before; the branch is chosen by the name they raise. Two callers that mean different
things no longer have to share one event name, so a genuine verification arriving alongside a resend
resumes the flow instead of being lost to whichever raise got there first.

If both events are already deliverable when the wait arms, the first branch declared wins, on every
runtime. Order the branches by what should take priority.

One caveat on Restate: its durable promises are write-once per event name per generation, so a branch
that can be raised repeatedly (a resend button is the usual case) only fires once unless the flow
takes a `Loop` after handling it. See the
[multi-branch row of the runtime matrix](../reference/runtime-matrix.md#gateway-semantics).

To make a specific raise idempotent, pass a stable `raiseId`; see the
[idempotent-raise row of the matrix](../reference/runtime-matrix.md#gateway-semantics) for per-engine
behavior.

## Put it together

The trigger seam becomes an ordinary operation on your entrypoint, and callers need nothing beyond the
business identity:

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

The examples' `IMembershipManager` ([`examples/`](../../examples/README.md)) is the worked version of
this seam, driven on all six runtimes as an interactive web control panel.

## Next

- [Authorize the gateway seam](authorize-the-gateway-seam.md) — enforce auth at this chokepoint and
  make ids unguessable.
- [Triggering reference](../reference/triggering.md) — exact signatures.
- [The triggering seam](../explanation/the-triggering-seam.md) — the design and its guarantees.
