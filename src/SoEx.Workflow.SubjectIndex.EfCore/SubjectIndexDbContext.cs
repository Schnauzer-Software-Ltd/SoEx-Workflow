using Microsoft.EntityFrameworkCore;

namespace SoEx.Workflow.SubjectIndex.EfCore;

/// <summary>
/// A subject↔instance edge — one row per <c>(subject, instance)</c> pair. The subject id is never stored in
/// clear: <see cref="SubjectToken"/> is its one-way lookup token (part of the key) and <see cref="SealedSubject"/>
/// is the subject id sealed under the instance's per-instance crypto-shred key — so the row's recoverable PII is
/// destroyed when that instance is shredded at termination.
/// </summary>
public sealed class SubjectEdge
{
    /// <summary>The one-way <see cref="ISubjectProtector.Tokenize"/> token — the at-rest lookup key (no plaintext).</summary>
    public string SubjectToken { get; set; } = "";

    public string InstanceId { get; set; } = "";

    /// <summary>The subject id sealed under the instance's per-instance key; opened to plaintext only in memory,
    /// and unrecoverable once the instance is crypto-shredded.</summary>
    public byte[] SealedSubject { get; set; } = [];
}

/// <summary>
/// The EF Core context backing <see cref="EfCoreSubjectIndex"/>: a single edge table with a composite key on
/// <c>(SubjectToken, InstanceId)</c> (so adds are naturally idempotent) and an index on <c>InstanceId</c> (so
/// termination pruning and <c>SubjectsFor</c> are index lookups). The subject id is stored only as a token + sealed
/// blob — never in clear. Provider-agnostic — the consumer configures the database provider via
/// <see cref="DbContextOptions{TContext}"/>.
/// </summary>
public sealed class SubjectIndexDbContext(DbContextOptions<SubjectIndexDbContext> options) : DbContext(options)
{
    public DbSet<SubjectEdge> Edges => Set<SubjectEdge>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureModel(modelBuilder);

    /// <summary>
    /// Maps the subject-edge entity. Exposed as a static so a combined context that backs the subject index and
    /// the maintenance logs from one database (the bundled <c>ErasureStores</c>) can apply this same mapping
    /// alongside the maintenance mapping.
    /// </summary>
    public static void ConfigureModel(ModelBuilder modelBuilder)
    {
        // Core mapping only (no relational ToTable) so the package stays provider-agnostic: the composite key
        // makes adds idempotent; the InstanceId index serves termination pruning and SubjectsFor.
        var edge = modelBuilder.Entity<SubjectEdge>();
        edge.HasKey(e => new { e.SubjectToken, e.InstanceId });
        edge.HasIndex(e => e.InstanceId);
    }
}
