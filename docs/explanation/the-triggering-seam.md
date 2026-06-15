> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# The triggering seam

Workflows are rarely driven by the code that started them. A webhook from an identity provider says
"this account was verified"; a payment processor says "this card was updated". These callers hold only
business identity (an org and an email, a subscriber id), with no instance handle, no flow knowledge,
and no key material. The triggering seam is what lets such a caller start and steer a flow. This page
explains its design and its guarantees. For the how-to, see
[Trigger flows from outside](../how-to/trigger-flows-from-outside.md).

## The problem: identity, not handles

A naive trigger API would hand the caller an instance id when the flow starts and expect them to keep
it. That falls apart immediately: the webhook that fires months later never saw the start, and the
payment callback runs in a different system. What every caller does share is the business identity of
the subject, so the seam is built to need only that.

## Deterministic ids

`DeterministicInstanceId.For(prefix, parts...)` turns a business identity into an instance id by hashing
it. Because it's a pure function, the code that starts the flow and the webhook that continues it derive
the same id from the same identity, with no lookup table, shared store, or handoff; the identity itself
is the only state involved.

The id is also PII-free by construction (it's a hash, not the email), which matters because instance ids
are journaled in clear and would otherwise leak the subject.

### Confirmable vs unguessable

There is a tension here. The unkeyed `For` is an unsalted hash truncated to 128 bits: deterministic,
non-secret, and therefore confirmable. Anyone holding a candidate identity can re-derive the id and
check it. That's what makes the start and continue sides agree without coordination, but it also means
the id alone is not a secret.

When you need the id to be unguessable by someone who knows the identity,
`DeterministicInstanceId.Keyed` derives it under a shared secret (HMAC-SHA256): still deterministic for
callers holding the secret, but not derivable or confirmable without it. The trade-off is distribution,
because every start/continue caller now needs the secret. Where that's impossible, the confirmable `For`
remains the pragmatic choice, and you lean on the authorization and cryptographic protections below.

## Sealing without the endpoint

Starting a flow needs a sealed seed, but the component reacting to a trigger is often the very component
the governed step dispatches into, so it can't also hold the dispatch endpoint. `WorkflowSealer` exists
to break that circularity. It is the seal side alone (key store + serializer + the operation name), so
trigger code can mint a seed without holding the machinery that runs it.

## One interface, different semantics per engine

`IWorkflowGateway` gives start and raise a uniform shape across every adapter, and the happy path
behaves identically (a conformance test enforces that). But two edge behaviors differ per engine:
whether a duplicate start throws, no-ops, or starts a second run, and whether a raise that arrives
before its wait is buffered or dropped. The docs surface this divergence rather than papering over it
(see the [gateway-semantics matrix](../reference/runtime-matrix.md#gateway-semantics)): an abstraction
that is known to leak is safer than one you wrongly assume is watertight. Design your caller for the
engine you target, or keep strictly to the happy path.

## Bare events

A bare "this happened" raise carries no payload, so how does the flow know what to do? A portable wait
can pre-decide: `WaitForEvent` takes an `OnEvent` continuation (the symmetric twin of `OnTimeout`),
sealed at wait time and journaled. The bare raise then resumes the wait into that pre-sealed step. This
is what lets a webhook raise an event at a flow with no flow knowledge and no key material at all. An
event raised with a sealed payload still wins and becomes the next step, so data-carrying events keep
working.

## Two lines of defense

The seam separates two concerns that are easy to conflate.

Authorization is policy, and the framework can't know your policy, so the gateway makes no access
decision of its own. What it provides is the chokepoint: supply an `IGatewayAuthorizer` and every start
and raise on every adapter consults it first. That turns "enforce auth somewhere upstream" into "enforce
auth in exactly one place", which is far easier to get right. The authorizer runs where the gateway
runs, so you still front the ingress at your edge; this is defense in depth.

Cryptographic integrity is something the framework can guarantee, and does: a payload-carrying
continuation is sealed under the per-instance key with the instance id bound in as associated data
(AAD). A payload forged for one instance, or replayed against another, fails at decrypt, because the AAD
bind makes the ciphertext inseparable from its instance. A bare (payloadless) event carries no such
proof, which is why authorization in front of it matters.

## See also

- [Authorize the gateway seam](../how-to/authorize-the-gateway-seam.md) — the practical wiring.
- [Triggering reference](../reference/triggering.md) — the exact types.
