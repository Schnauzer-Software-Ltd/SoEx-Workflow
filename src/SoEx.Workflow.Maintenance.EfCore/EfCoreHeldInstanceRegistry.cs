using Microsoft.EntityFrameworkCore;

namespace SoEx.Workflow.Maintenance.EfCore;

/// <summary>
/// Durable <see cref="IHeldInstanceRegistry"/> over EF Core — the quarantined-instance set as a relational table,
/// visible cross-process and across a restart. Provider supplied via <see cref="DbContextOptions{TContext}"/>;
/// a short-lived context per operation. Synchronous EF Core APIs (the interface is synchronous).
/// </summary>
public sealed class EfCoreHeldInstanceRegistry : IHeldInstanceRegistry
{
    private readonly Func<DbContext> _newContext;

    /// <param name="ensureCreated">Create the schema if absent (default). Pass false when migrations manage it.</param>
    public EfCoreHeldInstanceRegistry(DbContextOptions<MaintenanceDbContext> options, bool ensureCreated = true)
        : this(() => new MaintenanceDbContext(options ?? throw new ArgumentNullException(nameof(options))), ensureCreated)
    {
    }

    /// <summary>
    /// Drives the registry from a caller-supplied context factory rather than a fixed <see cref="MaintenanceDbContext"/>,
    /// so the bundled <c>ErasureStores</c> can back it from a combined context (one database for the maintenance
    /// logs and the subject index). The factory's context must map <see cref="HeldEntity"/>
    /// (via <see cref="MaintenanceDbContext.ConfigureModel"/>).
    /// </summary>
    /// <param name="contextFactory">Creates a fresh context per operation (each is short-lived and disposed).</param>
    public EfCoreHeldInstanceRegistry(Func<DbContext> contextFactory, bool ensureCreated = true)
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

    public void Record(HeldInstance held)
    {
        using DbContext db = New();
        HeldEntity? row = db.Set<HeldEntity>().Find(held.InstanceId);
        if (row is null)
        {
            db.Set<HeldEntity>().Add(new HeldEntity
            {
                InstanceId = held.InstanceId,
                DtoType = held.IdempotencyKey.DtoType,
                Sequence = held.IdempotencyKey.Sequence,
                Attempts = held.Attempts,
                HeldAt = held.HeldAt,
                LastError = held.LastError,
            });
        }
        else
        {
            row.DtoType = held.IdempotencyKey.DtoType;
            row.Sequence = held.IdempotencyKey.Sequence;
            row.Attempts = held.Attempts;
            row.HeldAt = held.HeldAt;
            row.LastError = held.LastError;
        }

        try
        {
            db.SaveChanges();
        }
        catch (DbUpdateException) when (HeldExists(held.InstanceId))
        {
            // A concurrent writer inserted the same instance id first — last write wins, and the filter confirms
            // the row is present, so this is the benign duplicate-key race. Any other DbUpdateException is a real
            // failure and propagates rather than being swallowed as a no-op.
        }
    }

    // Re-check on a fresh context whether the held row is present, to tell the expected duplicate-key race from
    // a genuine write failure.
    private bool HeldExists(string instanceId)
    {
        using DbContext db = New();
        return db.Set<HeldEntity>().Find(instanceId) is not null;
    }

    public void Clear(string instanceId)
    {
        using DbContext db = New();
        db.Set<HeldEntity>().Where(e => e.InstanceId == instanceId).ExecuteDelete();
    }

    public IReadOnlyCollection<HeldInstance> Held()
    {
        using DbContext db = New();
        return [.. db.Set<HeldEntity>()
            .Select(e => new { e.InstanceId, e.DtoType, e.Sequence, e.Attempts, e.HeldAt, e.LastError })
            .AsEnumerable()
            .Select(e => new HeldInstance(
                e.InstanceId, new IdempotencyKey(e.InstanceId, e.DtoType, e.Sequence), e.Attempts, e.HeldAt, e.LastError))];
    }
}
