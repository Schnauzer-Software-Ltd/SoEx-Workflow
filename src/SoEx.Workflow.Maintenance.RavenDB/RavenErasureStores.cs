using Raven.Client.Documents;
using SoEx.Workflow.SubjectIndex.RavenDB;

namespace SoEx.Workflow.Maintenance.RavenDB;

/// <summary>
/// The convenience default that backs the durable subject index AND the erasure-maintenance logs from <b>one</b>
/// RavenDB <see cref="IDocumentStore"/> — the two stores erasure routing always needs together, so a deployment
/// points one document store at both instead of running two. The interfaces stay separate: this hands out an
/// <see cref="ISubjectIndex"/>, an <see cref="IHeldInstanceRegistry"/>, and an
/// <see cref="IErasureRequestRegistry"/>/<see cref="IPendingErasureRequests"/>, so a consumer that wants real
/// separation can ignore this and construct the per-interface stores over their own document stores instead.
/// <para>
/// The RavenDB stores already isolate themselves by id/compare-exchange-key prefix, so sharing one document store
/// is pure composition — this distributes distinct sub-prefixes (<c>{prefix}subjidx/</c> and <c>{prefix}maint/</c>)
/// so the index and the maintenance logs never collide.
/// </para>
/// </summary>
public sealed class RavenErasureStores
{
    /// <param name="store">The shared document store backing both the index and the maintenance logs.</param>
    /// <param name="protector">Derives the PII-free at-rest token shared by the subject index and the request
    /// registry (the same one both stores would use standalone). Never the plaintext.</param>
    /// <param name="keys">The per-instance crypto-shred key store; the subject index seals each subject under its
    /// instance's key, so an indexed subject is shredded with its instance.</param>
    /// <param name="prefix">The shared id/key prefix isolating this erasure plumbing's documents; the index and the
    /// maintenance logs each get a distinct sub-prefix beneath it.</param>
    public RavenErasureStores(IDocumentStore store, ISubjectProtector protector, IInstanceKeyStore keys, string prefix = "erasure/")
    {
        ArgumentNullException.ThrowIfNull(store);
        SubjectIndex = new RavenDbSubjectIndex(store, protector, keys, prefix + "subjidx/");
        HeldInstances = new RavenDbHeldInstanceRegistry(store, prefix + "maint/");
        ErasureRequests = new RavenDbErasureRequestRegistry(store, protector, prefix + "maint/");
    }

    /// <summary>The durable subject→instance index, backed by the shared document store.</summary>
    public ISubjectIndex SubjectIndex { get; }

    /// <summary>The durable held-instance registry, backed by the shared document store.</summary>
    public IHeldInstanceRegistry HeldInstances { get; }

    /// <summary>The durable erasure-request registry. The single concrete satisfies both
    /// <see cref="IErasureRequestRegistry"/> (the open-request log) and <see cref="IPendingErasureRequests"/>
    /// (the async front door's intake) — one store, two faces.</summary>
    public RavenDbErasureRequestRegistry ErasureRequests { get; }
}
