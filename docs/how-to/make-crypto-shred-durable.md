> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# How to make crypto-shred durable

This guide replaces the in-memory governance stores with durable stores. Crypto-shred holds only when
the key store is durable. The key store must also be shared by each process that runs an instance: its
client, its orchestrator, and its step workers. The same rule applies to the subject index and the
idempotency store.

The bundled `InMemoryInstanceKeyStore` shreds only in one process. Use it for tests and demos only. It
gives no protection in production.

> For the reason that a shared store is necessary, see
> [Governance design](../explanation/governance-design.md).

## Swap the key store

Two production implementations of `IInstanceKeyStore` ship in separate packages. Both also implement
`IEnumerableInstanceKeyStore`. Thus the [abandoned-instance sweep](run-erasure-maintenance.md) works with
each of them.

1. Select a key store. Use OpenBao to keep the key on a dedicated secrets server. Use RavenDB if you
   already run RavenDB and want key liveness in the same place as your data.
2. At your composition root, replace `InMemoryInstanceKeyStore` with the durable key store.

No other code changes.

```csharp
// SoEx.Workflow.Keys.OpenBao — Transit engine; the key material never leaves the server.
// Encrypt/Decrypt are server-side calls; Destroy deletes the per-instance key (crypto-shred).
IInstanceKeyStore keys = new OpenBaoInstanceKeyStore(address: "https://openbao:8200", token: token);

// SoEx.Workflow.Keys.RavenDB — a per-instance data key, wrapped under a master key you supply, lives in
// compare-exchange; RavenDB is the single source of truth for key liveness, so Destroy on any app
// instance shreds cluster-wide. No key is cached.
IInstanceKeyStore keys = new RavenDbInstanceKeyStore(documentStore, masterKek /* 32 bytes */);
```

### The key store's own backups

`Destroy` removes a key from the live key store. A snapshot or backup of the key store from before the
destroy still contains the key. If you keep such a snapshot, it can reverse a shred. For RavenDB, the
risk is the snapshot together with the long-lived master KEK. For OpenBao, the risk is a storage
snapshot. Background:
[Crypto-shred and erasure](../explanation/crypto-shred-and-erasure.md#the-key-stores-own-backups).

> [!WARNING]
> Limit the retention of key-store snapshots. If a snapshot from before a destroy stays after the
> erasure window of an instance, a restore can reverse the shred. Do these operational steps:
>
> 1. Keep each key-store snapshot and backup for less time than the erasure window of an instance.
> 2. Keep the RavenDB master KEK in a KMS or an HSM. Do not keep it in the app configuration.
> 3. After a batch of shreds, call `RavenDbInstanceKeyStore.RotateKek`.
>
> `RotateKek` retires the old KEK. The keys in older snapshots then become permanently unwrappable.

The framework gives different help for each key store:

- **RavenDB.** RavenDB wraps each key with a master KEK that the client holds. Thus `RotateKek` makes a
  captured snapshot useless, with no external operation. The test suite proves this defeat end to end.
- **OpenBao.** OpenBao keeps the keys on the server. There is no client KEK to rotate. A restore of a
  storage snapshot from before a destroy brings back the Transit key. No client operation can undo that.
  You verify the OpenBao restore path by operation. The test suite does not verify it, because the path is
  on the server.

> [!WARNING]
> Make snapshot retention an SLO on OpenBao. On OpenBao, an operating condition limits the finality of
> the shred. You must own two conditions as a hard requirement:
>
> - The retention of snapshots and backups is shorter than your erasure deadline.
> - The custody of the unseal keys prevents an unauthorized restore.
>
> If a snapshot can stay after the erasure window, the shred is only as final as your retention policy.

## Swap the subject index

The subject index routes erasure from a subject to its instances. For durable routing across processes,
use a bundled durable store. Do not use `InMemorySubjectIndex`.

A durable index stores no recoverable subject id at rest. It uses two collaborators for this:

- An `ISubjectProtector`. It derives a one-way lookup token with no PII from a subject. The row key or
  the document key is that token. Use `HmacSubjectProtector` with a stable deployment secret.
- The same `IInstanceKeyStore`. The index seals each subject with the per-instance key of its instance.
  Thus the framework crypto-shreds the indexed subject together with the instance.

Both collaborators are necessary. A durable index rejects the pass-through `NullSubjectProtector`,
because that protector keeps plaintext at rest.

1. Get a stable deployment secret of 16 bytes or more from your secrets manager.
2. Create an `HmacSubjectProtector` with the secret.
3. Create a `RavenDbSubjectIndex` or an `EfCoreSubjectIndex` with the protector and the key store.

```csharp
// A stable deployment secret (≥16 bytes) that derives the index's lookup tokens. Keep it as durable as the
// index — losing or rotating it invalidates existing tokens. Source it from your secrets manager.
var protector = new HmacSubjectProtector(subjectTokenSecret);

ISubjectIndex index = new RavenDbSubjectIndex(documentStore, protector, keys);
// or, provider-agnostic — pass DbContextOptions for your EF Core provider:
var options = new DbContextOptionsBuilder<SubjectIndexDbContext>()
    .UseSqlite("Data Source=subject-index.db")
    .Options;
ISubjectIndex index = new EfCoreSubjectIndex(options, protector, keys);
```

> [!WARNING]
> Keep the subject-token secret as durable as the index. If you lose or rotate the secret, the existing
> tokens become invalid.

The durable registries for erasure requests use the same protector, for the same reason. See
[Run erasure maintenance](run-erasure-maintenance.md).

## Swap the idempotency store

Idempotency collapses at-least-once redelivery. If you wired idempotency, make its store durable too.

1. Replace the idempotency store with `RavenDbIdempotencyStore`.

```csharp
IIdempotencyStore idem = new RavenDbIdempotencyStore(documentStore);   // compare-exchange, exactly-once
```

The Elsa gateway also sends an idempotent re-raise through the idempotency store that you wired. See the
[gateway-semantics matrix](../reference/runtime-matrix.md#gateway-semantics).

## Wire all three durable stores

1. Create the durable key store, the subject protector, the subject index, and the idempotency store.
2. Wire `GovernedStep` and `GovernedTermination` with them, the same as before.

```csharp
IInstanceKeyStore keys      = new RavenDbInstanceKeyStore(documentStore, masterKek);
ISubjectProtector protector = new HmacSubjectProtector(subjectTokenSecret);
ISubjectIndex     index     = new RavenDbSubjectIndex(documentStore, protector, keys);
IIdempotencyStore idem      = new RavenDbIdempotencyStore(documentStore);
// …then wire GovernedStep/GovernedTermination exactly as before.
```

When all three stores are durable, the per-instance key, the erasure routing, and the exactly-once effects
stay after a restart. Each process can see them. Thus the shred holds across your full fleet.

## Optional: back the index and maintenance from one store

You always use the subject index and the erasure-maintenance logs together. The maintenance logs are the
held instances, the open requests, and the [pending intake](run-erasure-maintenance.md) of the async front
door. Each durable store type ships a bundle that keeps all of them in one store. For EF Core, the store is
one database. For RavenDB, the store is one document store.

1. Create `ErasureStores` (EF Core) or `RavenErasureStores` (RavenDB) with the protector and the key store.
2. Get the subject index, the held registry, and the request registry from the bundle.

```csharp
// EF Core (SoEx.Workflow.Maintenance.EfCore): one database behind the index and all three maintenance faces.
var stores = new ErasureStores(
    new DbContextOptionsBuilder<ErasureDbContext>().UseSqlite("Data Source=erasure.db").Options,
    protector, keys);

// RavenDB (SoEx.Workflow.Maintenance.RavenDB): one document store, prefix-isolated.
var stores = new RavenErasureStores(documentStore, protector, keys);

ISubjectIndex                index    = stores.SubjectIndex;
IHeldInstanceRegistry        held     = stores.HeldInstances;
EfCoreErasureRequestRegistry requests = stores.ErasureRequests; // satisfies IErasureRequestRegistry and IPendingErasureRequests
```

The bundle is a packaging convenience. The interfaces stay separate. To keep each store on its own
database, create the store for each interface as the sections above show.

The key store always stays separate from the bundle. If the key store shared that store, key liveness
would depend on it, and the shred would be weaker.

These facts apply to the bundle:

- Each store runs each operation in its own short transaction. The bundle shares one connection. It does
  not share one transaction. Thus the bundle alone does not make a prune-and-resolve atomic.
- The subject tokens of the index and of the request registry are in one store. Thus the
  [token-linkability](../reference/governance-services.md) note applies to that combined store.
- Both sets of tokens are one-way tokens with no PII. The separate key store seals each subject. Thus the
  combined store keeps no recoverable subject id at rest.

## Reference

- [Governance services](../reference/governance-services.md): each store interface and each bundled
  implementation.
- [Packages](../reference/packages.md): the package of each durable store.
