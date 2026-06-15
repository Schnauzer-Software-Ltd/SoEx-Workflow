using Microsoft.EntityFrameworkCore;
using SoEx.Workflow.SubjectIndex.EfCore;

namespace SoEx.Workflow.Maintenance.EfCore;

/// <summary>
/// The convenience default that backs the durable subject index AND the erasure-maintenance logs from <b>one</b>
/// EF Core database — the two stores erasure routing always needs together, so a deployment wires one connection
/// instead of two. The interfaces stay separate: this hands out an <see cref="ISubjectIndex"/>, an
/// <see cref="IHeldInstanceRegistry"/>, and an <see cref="IErasureRequestRegistry"/>/<see cref="IPendingErasureRequests"/>,
/// so a consumer that wants real separation can ignore this and construct the per-interface stores over their own
/// databases instead.
/// <para>
/// All three stores share a single <see cref="ErasureDbContext"/> factory (one set of tables in one database). The
/// schema is created once here; the stores are constructed with <c>ensureCreated: false</c> so they don't each
/// re-run it.
/// </para>
/// </summary>
public sealed class ErasureStores
{
    /// <param name="options">Options carrying the consumer's database provider, for the combined erasure context.</param>
    /// <param name="protector">Derives the PII-free at-rest token shared by the subject index and the request
    /// registry (the same one both stores would use standalone). Never the plaintext.</param>
    /// <param name="keys">The per-instance crypto-shred key store; the subject index seals each subject under its
    /// instance's key, so an indexed subject is shredded with its instance.</param>
    /// <param name="ensureCreated">Create the schema (all four tables) once if absent (default). Pass false when migrations manage it.</param>
    public ErasureStores(DbContextOptions<ErasureDbContext> options, ISubjectProtector protector, IInstanceKeyStore keys, bool ensureCreated = true)
    {
        ArgumentNullException.ThrowIfNull(options);
        DbContext Factory() => new ErasureDbContext(options);
        if (ensureCreated)
        {
            using DbContext db = Factory();
            db.Database.EnsureCreated();
        }

        // ensureCreated:false — the combined context above already created every table in one shot.
        SubjectIndex = new EfCoreSubjectIndex(Factory, protector, keys, ensureCreated: false);
        HeldInstances = new EfCoreHeldInstanceRegistry(Factory, ensureCreated: false);
        ErasureRequests = new EfCoreErasureRequestRegistry(Factory, protector, ensureCreated: false);
    }

    /// <summary>The durable subject→instance index, backed by the shared database.</summary>
    public ISubjectIndex SubjectIndex { get; }

    /// <summary>The durable held-instance registry, backed by the shared database.</summary>
    public IHeldInstanceRegistry HeldInstances { get; }

    /// <summary>The durable erasure-request registry. The single concrete satisfies both
    /// <see cref="IErasureRequestRegistry"/> (the open-request log) and <see cref="IPendingErasureRequests"/>
    /// (the async front door's intake) — one store, two faces.</summary>
    public EfCoreErasureRequestRegistry ErasureRequests { get; }
}
