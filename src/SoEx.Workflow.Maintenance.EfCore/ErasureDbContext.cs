using Microsoft.EntityFrameworkCore;
using SoEx.Workflow.SubjectIndex.EfCore;

namespace SoEx.Workflow.Maintenance.EfCore;

/// <summary>
/// One EF Core context mapping every entity the erasure plumbing persists — the subject-index edge plus the
/// three maintenance entities (held instance, open request, pending request) — so a single database can back the
/// subject index and the maintenance logs together. It applies the same mappings the standalone contexts use, via
/// <see cref="SubjectIndexDbContext.ConfigureModel"/> and <see cref="MaintenanceDbContext.ConfigureModel"/>, so the
/// collapsed layout is identical to the separate ones row-for-row.
/// <para>
/// Why a combined context (not two over one database): EF Core's <c>EnsureCreated</c> is all-or-nothing on the
/// database's existence, so a second context pointed at an already-created database never creates its tables. One
/// context that knows all the entities creates them in a single shot. Drives the bundled <see cref="ErasureStores"/>.
/// </para>
/// </summary>
public sealed class ErasureDbContext(DbContextOptions<ErasureDbContext> options) : DbContext(options)
{
    // The same DbSet names the standalone contexts expose, so the collapsed database uses the identical table
    // names (Edges/Held/Requests/Pending) — the combined layout is the separate ones side by side, not renamed.
    public DbSet<SubjectEdge> Edges => Set<SubjectEdge>();
    public DbSet<HeldEntity> Held => Set<HeldEntity>();
    public DbSet<RequestEntity> Requests => Set<RequestEntity>();
    public DbSet<PendingEntity> Pending => Set<PendingEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        SubjectIndexDbContext.ConfigureModel(modelBuilder);
        MaintenanceDbContext.ConfigureModel(modelBuilder);
    }
}
