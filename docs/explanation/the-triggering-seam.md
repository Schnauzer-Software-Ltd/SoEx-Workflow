> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# The triggering seam

The triggering seam lets an outside caller start and steer a flow. Usually, code other than the code
that started a workflow drives it. Two examples:

- A webhook from an identity provider says "this account was verified".
- A payment processor says "this card was updated".

Such a caller holds only business identity, for example an org and an email, or a subscriber id. It
holds no instance handle, no flow knowledge, and no key material. This page describes the design of the
seam and its guarantees. For the procedure, see
[Trigger flows from outside](../how-to/trigger-flows-from-outside.md).

## Business identity as the key

The seam needs only the business identity of the subject. All callers share this identity. An instance
id that the start gives to one caller does not reach the other callers. A webhook that fires months
later never saw the start. A payment callback runs in a different system.

## Deterministic ids

`DeterministicInstanceId.For(prefix, parts...)` hashes a business identity into an instance id. It is a
pure function. Thus the code that starts the flow and the webhook that continues it derive the same id
from the same identity. No lookup table, shared store, or handoff is necessary. The identity is the only
state.

The id is a hash of the identity, so it contains no PII. This is important because the journal keeps
instance ids in clear text. An id that contained the email would leak the subject.

### Confirmable vs unguessable

The unkeyed `For` is an unsalted hash truncated to 128 bits. It is deterministic and not secret, and
thus confirmable. A person who holds a candidate identity can derive the id again and check it. This
property lets the start side and the continue side agree with no coordination. It also means that the
id alone is not a secret.

`DeterministicInstanceId.Keyed` derives the id under a shared secret (HMAC-SHA256). Use it when a person
who knows the identity must not be able to guess the id. The id stays deterministic for callers that
hold the secret. Without the secret, nobody can derive or confirm the id. Thus each start caller and
each continue caller must have the secret. If you cannot distribute the secret, use the confirmable
`For`. Then rely on the authorization and cryptographic protections below.

## Seal without the endpoint

The start of a flow needs a sealed seed. The component that reacts to a trigger is often the component
that the governed step dispatches into. That component cannot also hold the dispatch endpoint.
`WorkflowSealer` breaks this circular dependency. It is the seal side alone: the key store, the
serializer, and the operation name. Trigger code uses it to mint a seed. Trigger code does not hold the
machinery that runs the seed.

## One interface, different semantics per runtime

`IWorkflowGateway` gives start and raise one shape on each adapter. On the happy path, all adapters
behave the same, and a conformance test enforces this. Two edge behaviors are different for each
runtime:

- A duplicate start throws, does nothing, or starts a second run.
- A raise that arrives before its wait is buffered or dropped.

The [gateway-semantics matrix](../reference/runtime-matrix.md#gateway-semantics) gives the behavior of
each runtime. An abstraction with known leaks is safer than an abstraction that you wrongly think has
none. Design your caller for the runtime that you target, or use only the happy path.

## Bare events

A bare raise says "this happened" and carries no payload. A portable wait decides in advance what the
flow does next. Each branch of a `WaitForEvent` carries an `OnEvent` continuation. `OnEvent` is the
branch-level equivalent of `OnTimeout`. The framework seals the continuation at wait time and journals
it. The bare raise then resumes the wait into the step that the branch sealed in advance. Thus a webhook
can raise an event at a flow with no flow knowledge and no key material.

A raise that carries data keeps the continuation of the branch. The continuation runs, and the data
reaches the step operation as a second argument. The two parties have these responsibilities:

- The flow decides what happens next, because only the flow knows its own state.
- The raiser supplies what it knows, because only the raiser knows that.

If a payload became the next step, the outside world would have to construct the internal state of the
flow. That is possible for a simple saga. It is impossible when the state is, for example, a statechart
snapshot. A branch that declares no continuation accepts a payload as the next step. Use this for the
cases where the raiser decides what happens next.

A wait can name several events. Each event has its own continuation, and all of them race the timer.
This is important for the seam because the callers are usually different systems. For example, an
identity provider confirms a verification, and an operator presses resend. Both raise at the same
parked instance. Give each caller its own event name. Then one caller cannot consume the raise of the
other caller.

## Two lines of defense

The seam keeps two concerns separate: authorization and cryptographic integrity.

Authorization is policy. The framework does not know your policy, so the gateway makes no access
decision of its own. The gateway gives you one place to enforce authorization. If you supply an
`IGatewayAuthorizer`, each start and each raise on each adapter calls it first. The authorizer runs
where the gateway runs. Also put a control on the ingress at your edge. Together, these two controls
give defense in depth.

The framework guarantees cryptographic integrity. A raise can carry one of two payloads. At a branch
that declares an `OnEvent`, the payload is event data. At a branch that declares no `OnEvent`, the
payload is the next step. The framework seals each payload with the per-instance key. The instance id is bound in as associated data
(AAD). If a payload is forged for one instance, or replayed against another, decryption fails. The AAD
binding makes the ciphertext inseparable from its instance. A bare event carries no payload, so it
carries no such proof. Thus authorization in front of a bare event is important.

## See also

- [Authorize the gateway seam](../how-to/authorize-the-gateway-seam.md) gives the wiring procedure.
- [Triggering reference](../reference/triggering.md) describes the types.
