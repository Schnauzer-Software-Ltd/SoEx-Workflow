> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Governance design

Three runtime-agnostic services back every governed step and termination: the per-instance key store,
the subject index, and the (optional) idempotency store. This page explains why these three, and how
they compose. For their API, see [Governance services](../reference/governance-services.md).

## Three services, one seam

All governance is applied at one seam (`GovernedStep` for each step, `GovernedTermination` at the
end) and wired identically into every runtime. That's deliberate: governance is only trustworthy if it
can't be bypassed. There's no "remember to also encrypt this" path for a consumer to forget, because
the seam does it.

Each service answers one question:

| Service | Question it answers |
|---|---|
| `IInstanceKeyStore` | *How is this instance's data made unreadable on demand?* |
| `ISubjectIndex` | *Which instances touch this person?* |
| `IIdempotencyStore` | *Did this step's effect already happen?* |

## The per-instance key

The key is scoped per instance rather than per subject, per tenant, or globally. That granularity is
what makes crypto-shred precise: destroying one instance's key forgets exactly that instance and
nothing else. A
coarser key would force you to choose between forgetting too much (a shared key) and tracking which
bytes belong to whom, which defeats the purpose.

The key is minted lazily on first use and hard-deleted at termination. `InMemoryInstanceKeyStore` is
AES-256-GCM. The interface is small on purpose (mint, seal, unseal, destroy, and "is it still live"),
so you can back it with anything that can hold and destroy a secret: a database, a KMS, an HSM,
OpenBao's Transit engine. The one hard requirement is that it be durable and shared (see
[crypto-shred and erasure](crypto-shred-and-erasure.md#what-makes-the-shred-hold-a-durable-shared-key-store));
everything else is a deployment choice.

## The subject index

Crypto-shred forgets an instance. A right-to-erasure request names a person. The subject index bridges
them: as each governed step runs with a `SubjectContext.Managed(...)`, the framework records an edge
from that subject to the instance. When a request arrives, `ErasureCoordinator` can then enumerate every
instance the subject ever touched, including instances the requester has no idea exist.

The index is additive and multi-subject (one instance may touch several people, and one person may be in
many instances), and it's pruned at termination so it doesn't grow without bound. Like the key store, it
must be durable and shared in production, or erasure routing can't see instances that ran on another
process.

Subjects can also be marked `External` instead of `Managed`, which tells the framework not to index or
erase them. Use that when another system owns the subject's lifecycle.

## Idempotency

Durable runtimes redeliver. An activity can run, crash before its result is recorded, and be retried, so
"exactly once" is a fiction unless something dedupes. The optional idempotency store collapses
at-least-once redelivery to a single effect, keyed on the `(InstanceId, DtoType, Sequence)` triple: the
same step at the same sequence applies its effect once, no matter how many times it's delivered.

It's optional because not every step is side-effecting, and because the right backing store depends on
your durability needs (in-memory for a single process, compare-exchange for exactly-once across a
fleet). When wired, the same triple also keys the idempotent outward write in `OnRetaining`, so
retention survives redelivery too.

## How they compose

A single governed step touches all three: it mints or uses the key to seal what it journals, indexes the
subject so erasure can find it later, and (if wired) checks the idempotency triple so a redelivery is a
no-op. The termination closes the loop, using the key store to shred and the subject index to prune.
Each service is independently swappable (an in-memory index with a RavenDB key store, or any other
mix), because the services meet only at the seam and never depend on each other.

## See also

- [Crypto-shred and erasure](crypto-shred-and-erasure.md) — what the key store ultimately enables.
- [Make crypto-shred durable](../how-to/make-crypto-shred-durable.md) — the production implementations.
