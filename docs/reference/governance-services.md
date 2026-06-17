> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Reference — governance services

Three runtime-agnostic services back the per-step and termination governance, wired into every runtime
through [`GovernedStep`/`GovernedTermination`](governed-core.md). Namespaces: `SoEx.Workflow`,
`SoEx.Workflow.Runtime.InMemory`. For swapping in durable implementations see
[Make crypto-shred durable](../how-to/make-crypto-shred-durable.md).

## `IInstanceKeyStore`

Mints a per-instance key on first use, encrypts/decrypts payloads under it, and hard-deletes it at termination
(crypto-shred).

| Member | Description |
|---|---|
| `Mint(string instanceId)` | Mint the per-instance key (idempotent). |
| `Has(string instanceId)` | Whether the key is still live (`true`) or shredded (`false`). |
| `Destroy(string instanceId)` | Hard-delete the key (crypto-shred). |
| encrypt / decrypt | Encrypt and decrypt payloads under the key (used by `SealStep` / `UnsealStep`). |

`IEnumerableInstanceKeyStore` adds `LiveInstances()` (the un-shredded set, with mint times), which the
[abandoned-instance sweep](erasure-api.md#erasuresweeploop) requires.

| Implementation | Package | Notes |
|---|---|---|
| `InMemoryInstanceKeyStore` | `SoEx.Workflow` | AES-256-GCM, in-process only. Implements `IEnumerableInstanceKeyStore`. Tests/demos. |
| `OpenBaoInstanceKeyStore` | `SoEx.Workflow.Keys.OpenBao` | OpenBao Transit; key material never leaves the server. Enumerable. |
| `RavenDbInstanceKeyStore` | `SoEx.Workflow.Keys.RavenDB` | Master-key-wrapped data key in compare-exchange; shreds cluster-wide. Enumerable. No key cached. |

## `ISubjectIndex`

Maps PII subject ids to instance ids for workflow-managed subjects (additive, multi-subject), so erasure
can find every instance touching a subject. Pruned at termination.

| Member | Description |
|---|---|
| `AddEdge(string subject, string instanceId)` | Index that an instance touches a subject. |
| `SubjectsFor(string instanceId)` | The subjects an instance is indexed under. |
| `RemoveInstance(string instanceId)` | Drop every subject→instance edge for an instance: the index prune that the termination runs after the crypto-shred (idempotent). |
| lookup by subject | Find the instances touching a subject (used by `ErasureCoordinator`). |

| Implementation | Package |
|---|---|
| `InMemorySubjectIndex` | `SoEx.Workflow` |
| `RavenDbSubjectIndex` | `SoEx.Workflow.SubjectIndex.RavenDB` |
| `EfCoreSubjectIndex` | `SoEx.Workflow.SubjectIndex.EfCore` (provider-agnostic) |

The durable implementations keep no recoverable subject at rest: each subject is stored as a one-way
`ISubjectProtector` token (the lookup key, derived from a deployment secret; PII-free, like a
`DeterministicInstanceId`) plus the plaintext sealed under that edge's per-instance key. The index
therefore inherits the instance's crypto-shred: when the instance's key is destroyed at termination,
its indexed subject becomes unrecoverable rather than merely row-deleted. These implementations take an
`ISubjectProtector` and the same `IInstanceKeyStore` the instances use; `InMemorySubjectIndex`
(single-process, RAM) needs neither.

The token is a keyed HMAC, so it is one-way — but, like any deterministic pseudonym, it carries two
residual properties to plan for. It is **confirmable**: an attacker who learns the deployment secret can
compute the token for a *guessed* subject and confirm whether that subject is indexed (the token resists
recovering an unknown subject, not confirming a known one). And it is **linkable**: the same subject
always yields the same token, so without ever recovering the plaintext an observer of the table can tell
that two instances touch the same person. Keep the `ISubjectProtector` secret in a secret manager,
separate from the key store's master key, and treat token equality as subject-linkable when reasoning
about what the at-rest index reveals.

The durable index and the erasure-maintenance logs are always wired together, so each backend ships a
bundle — `ErasureStores` (EF Core) / `RavenErasureStores` (RavenDB) — that backs the index and the
maintenance registries from one store while keeping the interfaces separate. See
[Back the index and maintenance from one store](../how-to/make-crypto-shred-durable.md#optional-back-the-index-and-maintenance-from-one-store).

## `IIdempotencyStore` (optional)

Collapses at-least-once step redelivery to a single effect on the
`(InstanceId, DtoType, Sequence)` triple (`IdempotencyKey`). The Elsa gateway also routes an idempotent
re-raise through whichever store is wired (see the
[gateway-semantics matrix](runtime-matrix.md#gateway-semantics)).

| Implementation | Package |
|---|---|
| `InMemoryIdempotencyStore` | `SoEx.Workflow` |
| `RavenDbIdempotencyStore` | `SoEx.Workflow.Idempotency.RavenDB` (compare-exchange, exactly-once) |

## `IdempotencyKey`

The `(InstanceId, DtoType, Sequence)` triple on which a step's effect is deduplicated; also used to key
the idempotent outward write in `OnRetaining`.

## See also

- [Governance design](../explanation/governance-design.md) — why these three, and how they compose.
