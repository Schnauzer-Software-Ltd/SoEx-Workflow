using System.Text;
using Microsoft.EntityFrameworkCore;

namespace SoEx.Workflow.SubjectIndex.EfCore;

/// <summary>
/// A durable, shared <see cref="ISubjectIndex"/> over EF Core — the subject→instance map erasure routing needs,
/// as a relational edge table. Provider-agnostic: the consumer supplies the database provider (SQL Server /
/// PostgreSQL / SQLite) in the <see cref="DbContextOptions{TContext}"/>, so the index lives wherever the
/// deployment already runs a relational database and is visible cross-process / across a restart.
/// <para>
/// A short-lived context per operation; the composite primary key makes <see cref="AddEdge"/> idempotent (a
/// duplicate insert is a no-op), and <see cref="RemoveInstance"/> is a single indexed <c>ExecuteDelete</c>.
/// Synchronous EF Core APIs throughout (no async bridge).
/// </para>
/// </summary>
public sealed class EfCoreSubjectIndex : ISubjectIndex
{
    private readonly Func<DbContext> _newContext;
    private readonly ISubjectProtector _protector;
    private readonly IInstanceKeyStore _keys;

    /// <param name="options">Configured options carrying the consumer's database provider.</param>
    /// <param name="protector">Derives the PII-free at-rest lookup token for a subject (never the plaintext).</param>
    /// <param name="keys">The per-instance crypto-shred key store (the same one the instances' data uses). Each
    /// edge's subject id is sealed under its instance's key, so the durable table holds only a one-way token and
    /// a blob that becomes unrecoverable when that instance is crypto-shredded at termination. Required: a durable
    /// index must not persist recoverable plaintext PII.</param>
    /// <param name="ensureCreated">Create the edge table if absent (default). Pass false when the schema is managed by migrations.</param>
    public EfCoreSubjectIndex(DbContextOptions<SubjectIndexDbContext> options, ISubjectProtector protector, IInstanceKeyStore keys, bool ensureCreated = true)
        : this(() => new SubjectIndexDbContext(options ?? throw new ArgumentNullException(nameof(options))), protector, keys, ensureCreated)
    {
    }

    /// <summary>
    /// Drives the index from a caller-supplied context factory rather than a fixed <see cref="SubjectIndexDbContext"/>,
    /// so the bundled <c>ErasureStores</c> can back the index from a combined context that maps the maintenance
    /// entities too (one database for the index and the maintenance logs). The factory's context must map
    /// <see cref="SubjectEdge"/> (via <see cref="SubjectIndexDbContext.ConfigureModel"/>).
    /// </summary>
    /// <param name="contextFactory">Creates a fresh context per operation (each is short-lived and disposed).</param>
    public EfCoreSubjectIndex(Func<DbContext> contextFactory, ISubjectProtector protector, IInstanceKeyStore keys, bool ensureCreated = true)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(keys);
        _newContext = contextFactory;
        _protector = DurableSubjectProtector.Require(protector, "a durable subject index");
        _keys = keys;
        if (ensureCreated)
        {
            using DbContext db = New();
            db.Database.EnsureCreated();
        }
    }

    private DbContext New() => _newContext();

    public void AddEdge(string subjectId, string instanceId)
    {
        string token = _protector.Tokenize(subjectId);
        using DbContext db = New();
        if (db.Set<SubjectEdge>().Any(e => e.SubjectToken == token && e.InstanceId == instanceId))
        {
            return; // idempotent — the edge already exists (a fresh seal would only re-encrypt the same subject).
        }

        // Seal the subject under the INSTANCE's per-instance key, so the edge inherits that instance's crypto-shred.
        db.Set<SubjectEdge>().Add(new SubjectEdge { SubjectToken = token, InstanceId = instanceId, SealedSubject = _keys.Encrypt(instanceId, Encoding.UTF8.GetBytes(subjectId)) });
        try
        {
            db.SaveChanges();
        }
        catch (DbUpdateException) when (EdgeExists(token, instanceId))
        {
            // A concurrent writer inserted the same (token, instance) pair first — the composite key rejected
            // our duplicate, which is the idempotent outcome we want. The filter confirms the edge is actually
            // present, so any OTHER DbUpdateException (connectivity, serialization, a different constraint) is a
            // real lost write and propagates rather than being masked as a no-op.
        }
    }

    // Re-check on a fresh context (not the failed one, whose change-tracker still holds the pending insert):
    // does the edge now exist? True ⇒ the failure was the expected duplicate-key race.
    private bool EdgeExists(string token, string instanceId)
    {
        using DbContext db = New();
        return db.Set<SubjectEdge>().Any(e => e.SubjectToken == token && e.InstanceId == instanceId);
    }

    public void RemoveInstance(string instanceId)
    {
        using DbContext db = New();
        db.Set<SubjectEdge>().Where(e => e.InstanceId == instanceId).ExecuteDelete();
    }

    public IReadOnlyCollection<string> InstancesFor(string subjectId)
    {
        string token = _protector.Tokenize(subjectId);
        using DbContext db = New();
        return db.Set<SubjectEdge>().Where(e => e.SubjectToken == token).Select(e => e.InstanceId).ToList();
    }

    public IReadOnlyCollection<string> SubjectsFor(string instanceId)
    {
        using DbContext db = New();
        var sealed_ = db.Set<SubjectEdge>().Where(e => e.InstanceId == instanceId).Select(e => e.SealedSubject).ToList();
        var subjects = new List<string>(sealed_.Count);
        foreach (byte[] blob in sealed_)
        {
            // Open under the instance's key. If the key is gone (the instance was crypto-shredded), the subject is
            // unrecoverable — skip it rather than surface a half-shredded edge.
            try { subjects.Add(Encoding.UTF8.GetString(_keys.Decrypt(instanceId, blob))); }
            catch (InvalidOperationException) { }
        }

        return subjects;
    }
}
