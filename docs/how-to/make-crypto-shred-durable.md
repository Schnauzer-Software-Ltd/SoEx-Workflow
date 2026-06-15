> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# How to make crypto-shred durable

Crypto-shred only holds if the per-instance key store is durable and shared across every process that
runs an instance: its client, orchestrator, and step workers. The bundled `InMemoryInstanceKeyStore`
shreds only within one process, which is fine for tests and demos and useless in production. This guide
swaps in a durable store. The same applies to the subject index and idempotency store.

> Why a shared store is required: [Governance design](../explanation/governance-design.md).

## Swap the key store

Two production `IInstanceKeyStore` implementations ship as separate packages; both also implement
`IEnumerableInstanceKeyStore`, so the [abandoned-instance sweep](run-erasure-maintenance.md) works
against either. Replace `InMemoryInstanceKeyStore` at your composition root; nothing else changes.

```csharp
// SoEx.Workflow.Keys.OpenBao — Transit engine; the key material never leaves the server.
// Encrypt/Decrypt are server-side calls; Destroy deletes the per-instance key (crypto-shred).
IInstanceKeyStore keys = new OpenBaoInstanceKeyStore(address: "https://openbao:8200", token: token);

// SoEx.Workflow.Keys.RavenDB — a per-instance data key, wrapped under a master key you supply, lives in
// compare-exchange; RavenDB is the single source of truth for key liveness, so Destroy on any app
// instance shreds cluster-wide. No key is cached.
IInstanceKeyStore keys = new RavenDbInstanceKeyStore(documentStore, masterKek /* 32 bytes */);
```

Pick OpenBao when you want the key to never leave a dedicated secrets server. Pick RavenDB when you
already run RavenDB and want the key liveness co-located with your data.

> **Operational caveat: the key store's own backups.** `Destroy` removes a key from the live store, but
> a key-store snapshot or backup taken before the destroy still contains it, so a retained pre-destroy
> snapshot can reverse a shred (for RavenDB, snapshot plus the long-lived master KEK; for OpenBao, a
> storage snapshot). Mitigate operationally: bound key-store snapshot/backup retention so none outlives
> an instance's erasure window; keep the RavenDB master KEK in a KMS/HSM, not app config; and after a
> batch of shreds call `RavenDbInstanceKeyStore.RotateKek`, which retires the old KEK so keys captured
> in older snapshots become permanently unwrappable. Background:
> [Crypto-shred and erasure](../explanation/crypto-shred-and-erasure.md#the-key-stores-own-backups).

## Swap the subject index

The subject index routes erasure (subject → instances). For durable, cross-process routing use a
bundled store instead of `InMemorySubjectIndex`.

A durable index never stores a recoverable subject id at rest. It needs two collaborators for that:

- an `ISubjectProtector`, which derives the PII-free, one-way lookup token from a subject (so the
  row/document key is never the plaintext subject). Use `HmacSubjectProtector`, seeded with a stable
  deployment secret;
- the same `IInstanceKeyStore`: the index seals each subject under its instance's per-instance key, so
  the indexed subject is crypto-shredded with the instance rather than merely row-deleted.

Both are required. A durable index rejects the pass-through `NullSubjectProtector`, which would leave
plaintext at rest.

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

The durable erasure-request registries take the same protector for the same reason; see
[Run erasure maintenance](run-erasure-maintenance.md).

## Swap the idempotency store

If you wired idempotency (to collapse at-least-once redelivery), make it durable too:

```csharp
IIdempotencyStore idem = new RavenDbIdempotencyStore(documentStore);   // compare-exchange, exactly-once
```

The Elsa gateway also routes an idempotent re-raise through whichever idempotency store is wired; see
the [gateway-semantics matrix](../reference/runtime-matrix.md#gateway-semantics).

## Together: a fully durable governance trio

```csharp
IInstanceKeyStore keys      = new RavenDbInstanceKeyStore(documentStore, masterKek);
ISubjectProtector protector = new HmacSubjectProtector(subjectTokenSecret);
ISubjectIndex     index     = new RavenDbSubjectIndex(documentStore, protector, keys);
IIdempotencyStore idem      = new RavenDbIdempotencyStore(documentStore);
// …then wire GovernedStep/GovernedTermination exactly as before.
```

With all three durable, the per-instance key, erasure routing, and exactly-once effects all survive a
restart and are visible cross-process, so the shred holds across your whole fleet.

## Optional: back the index and maintenance from one store

The subject index and the erasure-maintenance logs (held instances, open requests, and the async front
door's [pending intake](run-erasure-maintenance.md)) are always needed together, so each durable backend
ships a bundle that backs all of them from a single store — one EF Core database, or one RavenDB document
store — instead of wiring each separately:

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

The bundle is only a packaging convenience: the interfaces stay separate, so a consumer who wants each
store on its own backend keeps constructing the per-interface stores shown above. The per-instance key
store stays independent either way — co-locating it would couple key liveness to the same store and
weaken the shred.

Two things to know about the collapse. Each store still runs each operation in its own short-lived
transaction, so the bundle shares one connection, not one transaction — it does not by itself make a
prune-and-resolve atomic. And because the index's subject tokens and the request registry's subject
tokens now sit in one store, the [token-linkability](../reference/governance-services.md) note applies
across that combined store; both remain PII-free one-way tokens with the subject sealed under the
(separate) key store, so there is still no recoverable subject id at rest.

## Reference

- [Governance services](../reference/governance-services.md) — every store interface and bundled
  implementation.
- [Packages](../reference/packages.md) — which package each durable store ships in.
