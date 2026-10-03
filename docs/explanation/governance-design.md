> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Governance design

Governance is the set of controls that the framework applies to each governed step and each governed
termination. Three runtime-agnostic services supply these controls:

- the per-instance key store
- the subject index
- the idempotency store, which is optional.

This page describes the role of each service and how the services work together. For their API, see
[Governance services](../reference/governance-services.md).

## Three services, one seam

The framework applies all governance at one seam: `GovernedStep` for each step, and
`GovernedTermination` at the end. Each runtime wires this seam in the same way. Governance is
trustworthy only if no consumer can bypass it. The seam does the encryption, so consumer code has no
encryption step to forget.

Each service answers one question:

| Service | Question it answers |
|---|---|
| `IInstanceKeyStore` | *How does the data of this instance become unreadable on demand?* |
| `ISubjectIndex` | *Which instances touch this person?* |
| `IIdempotencyStore` | *Did the effect of this step already occur?* |

## The per-instance key

The scope of each key is one instance. When the framework destroys the key of one instance, it forgets
that instance and nothing else. Thus crypto-shred is precise. A key with a larger scope, such as per
subject, per tenant, or global, forces a choice between two bad results. A shared key makes a destroy
forget too much. The alternative is to track which bytes belong to which person, and that defeats the
purpose.

The key store mints the key lazily on first use. It hard-deletes the key at termination.
`InMemoryInstanceKeyStore` uses AES-256-GCM. The interface is small by design. It has five operations:
mint, seal, unseal, destroy, and a check that the key is still live. Thus any store that can hold and
destroy a secret can back it: a database, a KMS, an HSM, or the Transit engine of OpenBao. The key store
must be durable and shared. See
[crypto-shred and erasure](crypto-shred-and-erasure.md#what-makes-the-shred-hold-a-durable-shared-key-store).
All other properties of the key store are deployment choices.

## The subject index

Crypto-shred forgets an instance. A right-to-erasure request names a person. The subject index connects
the person to the instances. When a governed step runs with a `SubjectContext.Managed(...)`, the
framework records an edge from that subject to the instance. When a request arrives,
`ErasureCoordinator` lists each instance that the subject touched. The list includes instances that the
requester does not know about.

The index is additive and multi-subject. One instance can touch several people, and one person can be
in many instances. The framework prunes the index at termination, so the index does not grow without
limit. A step adds to the index when it declares, on the action that it returns, the people that it
learned about. The framework indexes these subjects immediately. Thus an erasure request that arrives
while the instance is parked still reaches the instance.

The framework also carries these subjects onto the sealed continuation. From that point, the name
guards cover them. In production, the index must be durable and shared, as the key store must be. If
the index is not durable and shared, erasure routing cannot see instances that ran in another process.

You can mark a subject `External` in the place of `Managed`. The framework then does not index or erase
that subject. Use `External` when another system owns the lifecycle of the subject.

## Idempotency

Durable runtimes redeliver. An activity can run, crash before the runtime records its result, and run
again. Exactly-once execution needs deduplication. The optional idempotency store collapses
at-least-once redelivery to one effect. Its key is the `(InstanceId, DtoType, Sequence)` triple. The
same step at the same sequence applies its effect one time, for any number of deliveries.

The idempotency store is optional for two reasons:

- Some steps have no side effects.
- The correct backing store depends on your durability needs. Use in-memory for one process. Use
  compare-exchange for exactly-once across a fleet.

If you wire the idempotency store, the same triple also keys the idempotent outward write in
`OnRetaining`. Thus retention also survives redelivery.

## How they compose

One governed step uses all three services:

- It mints or uses the key to seal the data that it journals.
- It indexes the subject, so that erasure can find the subject later.
- If the idempotency store is wired, it checks the triple, so a redelivery has no effect.

The termination completes the lifecycle. It uses the key store to shred and the subject index to prune.
You can replace each service independently. An example is an in-memory index with a RavenDB key store.
Any other combination is also possible. The services meet only at the seam and do not depend on each
other.

## See also

- [Crypto-shred and erasure](crypto-shred-and-erasure.md) describes what the key store enables.
- [Make crypto-shred durable](../how-to/make-crypto-shred-durable.md) gives the production
  implementations.
