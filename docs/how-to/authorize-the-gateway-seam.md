> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# How to authorize the gateway seam

The trigger gateway is the one place through which each start and each raise goes. See
[Trigger flows from outside](trigger-flows-from-outside.md). This guide shows how to enforce
authorization at the gateway. It also shows how to make instance ids unguessable. For the design, see
[The triggering seam](../explanation/the-triggering-seam.md).

## Enforce authorization at the gateway

You own the authorization rules. The gateway calls your rules for each operation. Before a gateway starts
a flow or raises an event, it calls the `IGatewayAuthorizer` that you supply. If the authorizer throws, the
gateway rejects the operation.

1. Write a class that implements `IGatewayAuthorizer`.
2. In `AuthorizeStartAsync` and `AuthorizeRaiseEventAsync`, throw if the caller has no authorization.
3. Pass an instance of the class to the gateway constructor.

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

Authorization then occurs in one place on each adapter. The throw rejects the operation before the
gateway calls the runtime. If you supply no authorizer, the gateway allows all operations.

> [!CAUTION]
> Keep access control at your ingress too. The authorizer runs in the process of the gateway. Your
> network edge needs its own access control. Use the authorizer as one layer of defense in depth.

## Make instance ids unguessable

`DeterministicInstanceId.For` is an unsalted hash of the identity, truncated to 128 bits. The id is not
secret, and it is confirmable. A caller with a candidate org and email can derive the id again. This is
the design: the start side and the continue side get the same id with no shared store. Thus a person can
confirm a guess.

If a person who knows the identity must not be able to guess the id, derive the id with a shared secret
(HMAC-SHA256).

1. Give the shared HMAC key to each caller.
2. Call `DeterministicInstanceId.Keyed` with the secret, the flow prefix, and the identity.

```csharp
ReadOnlySpan<byte> secret = sharedSecretBytes;   // the shared HMAC key, distributed to every caller
string id = DeterministicInstanceId.Keyed(secret, "onboard", orgId, email);
// deterministic for callers holding the secret; not derivable or confirmable without it
```

The keyed id is deterministic for the start side and the continue side that hold the secret. You must
give the secret to each caller. If you cannot do that, use the unkeyed `For`.

## What the framework protects with cryptography

The framework seals the data of each raise with the per-instance key. The seal binds the instance id as
associated data (AAD). If a payload is forged or comes from a different instance, decryption fails. This
protection is cryptography, separate from access control.

A bare event has no payload, so it carries no such proof. Thus keep authorization in front of bare
events.

## Reference

- [Triggering reference](../reference/triggering.md): `IGatewayAuthorizer`, `DeterministicInstanceId`.
- [The triggering seam](../explanation/the-triggering-seam.md): confirmable ids and keyed ids, the AAD
  bind.
