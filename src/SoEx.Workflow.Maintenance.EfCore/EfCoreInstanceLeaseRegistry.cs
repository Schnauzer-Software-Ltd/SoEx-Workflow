using Microsoft.EntityFrameworkCore;

namespace SoEx.Workflow.Maintenance.EfCore;

/// <summary>
/// Durable <see cref="IInstanceLeaseRegistry"/> over EF Core — one row per leased instance, visible cross-process
/// and across a restart, so the step workers that renew and the sweep that reads can run in different processes.
/// Provider supplied via <see cref="DbContextOptions{TContext}"/>; a short-lived context per operation. Synchronous
/// EF Core APIs (the interface is synchronous).
/// </summary>
public sealed class EfCoreInstanceLeaseRegistry : IInstanceLeaseRegistry
{
    private readonly Func<DbContext> _newContext;

    /// <param name="ensureCreated">Create the schema if absent (default). Pass false when migrations manage it.</param>
    public EfCoreInstanceLeaseRegistry(DbContextOptions<MaintenanceDbContext> options, bool ensureCreated = true)
        : this(() => new MaintenanceDbContext(options ?? throw new ArgumentNullException(nameof(options))), ensureCreated)
    {
    }

    /// <summary>
    /// Drives the registry from a caller-supplied context factory, so the bundled <c>ErasureStores</c> can back it
    /// from the combined context. The factory's context must map <see cref="LeaseEntity"/>
    /// (via <see cref="MaintenanceDbContext.ConfigureModel"/>).
    /// </summary>
    /// <param name="contextFactory">Creates a fresh context per operation (each is short-lived and disposed).</param>
    public EfCoreInstanceLeaseRegistry(Func<DbContext> contextFactory, bool ensureCreated = true)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        _newContext = contextFactory;
        if (ensureCreated)
        {
            using DbContext db = New();
            db.Database.EnsureCreated();
        }
    }

    private DbContext New() => _newContext();

    public void Renew(InstanceLease lease)
    {
        using DbContext db = New();
        LeaseEntity? row = db.Set<LeaseEntity>().Find(lease.InstanceId);
        if (row is null)
        {
            db.Set<LeaseEntity>().Add(new LeaseEntity
            {
                InstanceId = lease.InstanceId,
                RenewedAt = lease.RenewedAt,
                ExpiresAt = lease.ExpiresAt,
            });
        }
        else
        {
            row.RenewedAt = lease.RenewedAt;
            row.ExpiresAt = lease.ExpiresAt;
        }

        try
        {
            db.SaveChanges();
        }
        catch (DbUpdateException) when (LeaseExists(lease.InstanceId))
        {
            // A concurrent step of the same instance inserted the row first — last write wins, and the filter
            // confirms the row is present, so this is the benign duplicate-key race. Any other DbUpdateException is a
            // real failure and propagates.
        }
    }

    private bool LeaseExists(string instanceId)
    {
        using DbContext db = New();
        return db.Set<LeaseEntity>().Find(instanceId) is not null;
    }

    public void Forget(string instanceId)
    {
        using DbContext db = New();
        db.Set<LeaseEntity>().Where(e => e.InstanceId == instanceId).ExecuteDelete();
    }

    public IReadOnlyCollection<InstanceLease> Leases()
    {
        using DbContext db = New();
        return [.. db.Set<LeaseEntity>()
            .Select(e => new { e.InstanceId, e.RenewedAt, e.ExpiresAt })
            .AsEnumerable()
            .Select(e => new InstanceLease(e.InstanceId, e.RenewedAt, e.ExpiresAt))];
    }
}
