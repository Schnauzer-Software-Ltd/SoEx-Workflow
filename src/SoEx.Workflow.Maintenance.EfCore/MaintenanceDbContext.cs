using Microsoft.EntityFrameworkCore;

namespace SoEx.Workflow.Maintenance.EfCore;

/// <summary>A held instance row (keyed on <see cref="InstanceId"/>).</summary>
public sealed class HeldEntity
{
    public string InstanceId { get; set; } = "";
    public string DtoType { get; set; } = "";
    public long Sequence { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset HeldAt { get; set; }
    public string? LastError { get; set; }
}

/// <summary>An open erasure-request row (keyed on <see cref="RequestId"/>). The id lists are stored as
/// newline-joined strings so the model needs no relational child-table / owned-collection mapping and stays
/// provider-agnostic.</summary>
public sealed class RequestEntity
{
    public string RequestId { get; set; } = "";
    public DateTimeOffset Deadline { get; set; }
    public string OpenInstanceIds { get; set; } = "";

    /// <summary>The request's subjects as newline-joined one-way <see cref="ISubjectProtector"/> tokens — never
    /// recoverable plaintext (the registry routes deadline review by instance id, so it never needs the subject back).</summary>
    public string Subjects { get; set; } = "";

    /// <summary>Optimistic-concurrency token. Bumped on every write so two concurrent per-instance
    /// <c>Resolve</c>s of one request can't lose an update (one fails the version check and retries).</summary>
    public Guid Version { get; set; }
}

/// <summary>An admitted-but-not-yet-drained erasure-request row (keyed on <see cref="RequestId"/>). Carries only
/// the PII-free instance ids resolved at admit (newline-joined), so the async front door's durable intake holds
/// no recoverable subject at rest.</summary>
public sealed class PendingEntity
{
    public string RequestId { get; set; } = "";
    public DateTimeOffset ReceivedAt { get; set; }
    public string InstanceIds { get; set; } = "";
}

/// <summary>
/// The EF Core context backing the durable maintenance logs: a held-instance table, an open-request table, and
/// the async front door's pending-request table. Core mapping only (no relational <c>ToTable</c>) so the package
/// stays provider-agnostic; the consumer configures the database provider via <see cref="DbContextOptions{TContext}"/>.
/// </summary>
public sealed class MaintenanceDbContext(DbContextOptions<MaintenanceDbContext> options) : DbContext(options)
{
    public DbSet<HeldEntity> Held => Set<HeldEntity>();
    public DbSet<RequestEntity> Requests => Set<RequestEntity>();
    public DbSet<PendingEntity> Pending => Set<PendingEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureModel(modelBuilder);

    /// <summary>
    /// Maps the held-instance, open-request, and pending-request entities. Exposed as a static so a combined
    /// context that backs the maintenance logs and the subject index from one database (the bundled
    /// <c>ErasureStores</c>) can apply this same mapping alongside the subject-index mapping.
    /// </summary>
    public static void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<HeldEntity>().HasKey(e => e.InstanceId);
        modelBuilder.Entity<RequestEntity>().HasKey(e => e.RequestId);
        modelBuilder.Entity<RequestEntity>().Property(e => e.Version).IsConcurrencyToken();
        modelBuilder.Entity<PendingEntity>().HasKey(e => e.RequestId);
    }
}
