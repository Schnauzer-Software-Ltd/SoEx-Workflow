> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Reference: governance services

Three services support the governance of each step and of the termination. They work the same on each
runtime. [`GovernedStep`/`GovernedTermination`](governed-core.md) connects them to each runtime.
Namespaces: `SoEx.Workflow`, `SoEx.Workflow.Runtime.InMemory`. To use durable implementations, see
[Make crypto-shred durable](../how-to/make-crypto-shred-durable.md).

## `IInstanceKeyStore`

`IInstanceKeyStore` mints a per-instance key on first use. It encrypts and decrypts payloads with that
key. At termination, it hard-deletes the key (crypto-shred).

| Member | Description |
|---|---|
| `Mint(string instanceId)` | Mints the per-instance key (idempotent). |
| `Has(string instanceId)` | Returns `true` if the key is live, and `false` after the shred. |
| `Destroy(string instanceId)` | Hard-deletes the key (crypto-shred). |
| encrypt / decrypt | Encrypt and decrypt payloads with the key. `SealStep` and `UnsealStep` use them. |

`IEnumerableInstanceKeyStore` adds `LiveInstances()`. It returns the set of keys that are not shredded,
with their mint times. The [abandoned-instance sweep](erasure-api.md#erasuresweeploop) requires it.

| Implementation | Package | Notes |
|---|---|---|
| `InMemoryInstanceKeyStore` | `SoEx.Workflow` | AES-256-GCM, in-process only. Implements `IEnumerableInstanceKeyStore`. For tests and demos. |
| `OpenBaoInstanceKeyStore` | `SoEx.Workflow.Keys.OpenBao` | OpenBao Transit. The key material stays on the server. Enumerable. |
| `RavenDbInstanceKeyStore` | `SoEx.Workflow.Keys.RavenDB` | A data key, wrapped with a master key, in compare-exchange. The shred applies to the full cluster. Enumerable. Caches no key. |

## `ISubjectIndex`

`ISubjectIndex` maps PII subject ids to instance ids for workflow-managed subjects. It is additive, and
an instance can have many subjects. Erasure uses it to find each instance that holds a subject. The
termination prunes it.

| Member | Description |
|---|---|
| `AddEdge(string subject, string instanceId)` | Records that an instance holds a subject. |
| `SubjectsFor(string instanceId)` | Returns the subjects of an instance in the index. |
| `RemoveInstance(string instanceId)` | Removes each subject→instance edge of an instance. The termination runs this index prune after the crypto-shred (idempotent). |
| `InstancesFor(string subject)` | Returns the instances that hold a subject. `ErasureCoordinator` uses it. The flow-scoped `InstancesForAsync` of the utility also uses it. |

| Implementation | Package |
|---|---|
| `InMemorySubjectIndex` | `SoEx.Workflow` |
| `RavenDbSubjectIndex` | `SoEx.Workflow.SubjectIndex.RavenDB` |
| `EfCoreSubjectIndex` | `SoEx.Workflow.SubjectIndex.EfCore` (provider-agnostic) |

### How the durable index stores a subject

The durable implementations keep no recoverable subject at rest. For each edge, they store two values:

- A one-way `ISubjectProtector` token. This is the lookup key. It comes from a deployment secret. It is
  PII-free, like a `DeterministicInstanceId`.
- The plaintext subject, sealed with the per-instance key of that edge.

Thus, the index entry has the same crypto-shred as the instance. When the termination destroys the key of
the instance, the indexed subject becomes unrecoverable. This protection does not depend on a row deletion.
These implementations take an `ISubjectProtector` and the `IInstanceKeyStore` that the instances use.
`InMemorySubjectIndex` is single-process and in RAM, and it needs neither.

### Properties of the token

The token is a keyed HMAC, so it is one-way. It is a deterministic pseudonym, so it has two more
properties:

- **The token is confirmable.** An attacker with the deployment secret can calculate the token for a
  guessed subject. The attacker can then confirm if that subject is in the index. The token prevents the
  recovery of an unknown subject only.
- **The token is linkable.** One subject always gives the same token. Thus, a person who can see the
  table can see that two instances hold the same person. That person does not need the plaintext.

Keep the `ISubjectProtector` secret in a secret manager, separate from the master key of the key store.
When you analyze what the index at rest shows, consider equal tokens as a link to one subject.

### Stores for the index and the maintenance logs

You always wire the durable index and the erasure-maintenance logs together. Thus, each store type has a
bundle: `ErasureStores` (EF Core) and `RavenErasureStores` (RavenDB). A bundle puts the index and the
maintenance registries in one store, and the interfaces stay separate. See
[Back the index and maintenance from one store](../how-to/make-crypto-shred-durable.md#optional-back-the-index-and-maintenance-from-one-store).

## `IIdempotencyStore` (optional)

`IIdempotencyStore` applies the effect of a step one time for each `(InstanceId, DtoType, Sequence)`
triple (`IdempotencyKey`). An at-least-once redelivery then has no second effect. The Elsa gateway also
sends an idempotent re-raise through the wired store. See the
[gateway-semantics matrix](runtime-matrix.md#gateway-semantics).

Delivery is effectively-once. In normal operation, the effect of a step runs one time for each triple.
A crash can occur after the effect of the consumer commits and before the store records the effect as
`done`. In this case, the store keeps a `pending` claim. After `StealAfter` (default 2 minutes), another
run takes the claim and runs the effect again. Thus, after a crash, delivery is at-least-once.

These rules apply:

- Make your effect idempotent.
- Set `StealAfter` to a value that is longer than your slowest step.
- If the deduplication must continue after a restart, wire the durable store. The in-memory store loses
  its records on a restart.

| Implementation | Package |
|---|---|
| `InMemoryIdempotencyStore` | `SoEx.Workflow` |
| `RavenDbIdempotencyStore` | `SoEx.Workflow.Idempotency.RavenDB` (compare-exchange. Effectively-once. At-least-once after a crash in the [effect-commit … done-write] window.) |

## `IdempotencyKey`

`IdempotencyKey` is the `(InstanceId, DtoType, Sequence)` triple. The framework uses it to apply the
effect of a step one time. It is also the key for the idempotent outward write in `OnRetaining`.

## Related pages

- [Governance design](../explanation/governance-design.md): the reasons for these three services, and how
  they work together.
