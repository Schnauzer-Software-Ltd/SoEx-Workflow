> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# How to authorize the gateway seam

The trigger gateway ([Trigger flows from outside](trigger-flows-from-outside.md)) is the one place
every start and raise passes through. This guide shows how to enforce authorization there and how to
make instance ids unguessable. For the reasoning, see
[The triggering seam](../explanation/the-triggering-seam.md).

## Enforce authorization at the chokepoint

The gateway makes no policy decision of its own (the framework can't know your auth rules), but it
owns the chokepoint for them. Supply an `IGatewayAuthorizer` and every gateway consults it before it
starts a flow or raises an event; a throw rejects the operation:

```csharp
sealed class TokenAuthorizer(Func<string> currentToken) : IGatewayAuthorizer
{
    public Task AuthorizeStartAsync(string instanceId) => Require();
    public Task AuthorizeRaiseEventAsync(string instanceId, string eventName) => Require();
    Task Require() => IsValid(currentToken()) ? Task.CompletedTask
        : throw new UnauthorizedAccessException("gateway operation not authorized");
}

// pass it to any gateway ctor; omit it (the default) to allow everything, as before
var gateway = new InProcWorkflowGateway<IOnboard>(step, termination, new TokenAuthorizer(AmbientToken));
```

This enforces authorization in one place on every adapter instead of "somewhere upstream". The throw
rejects the operation before the runtime is touched. With no authorizer the gateway allows everything.

> The authorizer runs where the gateway runs, not at your network edge, so you should still front the
> ingress. Treat it as defense in depth rather than your only line.

## Make instance ids unguessable

`DeterministicInstanceId.For` is an unsalted hash of the identity (truncated to 128 bits), so it's
non-secret and confirmable: a caller with a candidate org and email can re-derive it. That's by design,
so the start and continue sides agree without a shared store, but it means a guess can be confirmed.

When the id must be unguessable by someone who knows the identity, derive it under a shared secret
(HMAC-SHA256) instead:

```csharp
ReadOnlySpan<byte> secret = sharedSecretBytes;   // the shared HMAC key, distributed to every caller
string id = DeterministicInstanceId.Keyed(secret, "onboard", orgId, email);
// deterministic for callers holding the secret; not derivable or confirmable without it
```

It's equally deterministic for the start and continue sides that hold the secret. The cost is
distributing the secret to every caller; where that's impossible, the unkeyed `For` remains.

## What the framework guarantees regardless

One protection is cryptographic rather than access control: a payload-carrying continuation is sealed
under the per-instance key with the instance id bound in as associated data, so a forged or
cross-instance payload fails at decrypt. A bare (payloadless) event carries no such proof, which is
another reason to keep auth in front of it.

## Reference

- [Triggering reference](../reference/triggering.md) — `IGatewayAuthorizer`, `DeterministicInstanceId`.
- [The triggering seam](../explanation/the-triggering-seam.md) — confirmable vs keyed ids, the AAD bind.
