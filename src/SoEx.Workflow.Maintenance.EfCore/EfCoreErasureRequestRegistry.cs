using Microsoft.EntityFrameworkCore;

namespace SoEx.Workflow.Maintenance.EfCore;

/// <summary>
/// Durable <see cref="IErasureRequestRegistry"/> over EF Core — open erasure requests as a relational table,
/// visible cross-process and across a restart. Provider supplied via <see cref="DbContextOptions{TContext}"/>;
/// a short-lived context per operation. The instance-id and subject lists are stored as newline-joined strings.
/// <para>
/// The subjects of a person actively exercising erasure are <b>tokenized</b> via the <see cref="ISubjectProtector"/>
/// before they are written, so the row holds only the same one-way, PII-free token a durable
/// <see cref="ISubjectIndex"/> uses — never a recoverable subject at rest. The registry never needs the plaintext
/// back (deadline review routes by instance id, not subject), so a one-way token suffices. The token returned by
/// <see cref="Open()"/> is residue, exactly like an instance id; it is not the plaintext subject.
/// </para>
/// </summary>
public sealed class EfCoreErasureRequestRegistry : IErasureRequestRegistry, IPendingErasureRequests
{
    private const char Sep = '\n';

    private readonly Func<DbContext> _newContext;
    private readonly ISubjectProtector _protector;

    /// <param name="protector">Derives the PII-free at-rest token for each subject (never the plaintext). Required:
    /// a durable registry must not persist a recoverable subject of someone exercising erasure. Use the same
    /// <see cref="HmacSubjectProtector"/> the durable subject index uses.</param>
    /// <param name="ensureCreated">Create the schema if absent (default). Pass false when migrations manage it.</param>
    public EfCoreErasureRequestRegistry(DbContextOptions<MaintenanceDbContext> options, ISubjectProtector protector, bool ensureCreated = true)
        : this(() => new MaintenanceDbContext(options ?? throw new ArgumentNullException(nameof(options))), protector, ensureCreated)
    {
    }

    /// <summary>
    /// Drives the registry from a caller-supplied context factory rather than a fixed <see cref="MaintenanceDbContext"/>,
    /// so the bundled <c>ErasureStores</c> can back it from a combined context (one database for the maintenance
    /// logs and the subject index). The factory's context must map <see cref="RequestEntity"/> and
    /// <see cref="PendingEntity"/> (via <see cref="MaintenanceDbContext.ConfigureModel"/>).
    /// </summary>
    /// <param name="contextFactory">Creates a fresh context per operation (each is short-lived and disposed).</param>
    public EfCoreErasureRequestRegistry(Func<DbContext> contextFactory, ISubjectProtector protector, bool ensureCreated = true)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        _newContext = contextFactory;
        _protector = DurableSubjectProtector.Require(protector, "a durable erasure-request registry");
        if (ensureCreated)
        {
            using DbContext db = New();
            db.Database.EnsureCreated();
        }
    }

    private DbContext New() => _newContext();

    public void Open(OpenErasureRequest request)
    {
        using DbContext db = New();
        RequestEntity? row = db.Set<RequestEntity>().Find(request.RequestId);
        if (row is null)
        {
            db.Set<RequestEntity>().Add(new RequestEntity
            {
                RequestId = request.RequestId,
                Deadline = request.Deadline,
                OpenInstanceIds = Join(request.OpenInstanceIds),
                Subjects = Tokenize(request.Subjects),
                Version = Guid.NewGuid(),
            });
        }
        else
        {
            row.Deadline = request.Deadline;
            row.OpenInstanceIds = Join(request.OpenInstanceIds);
            row.Subjects = Tokenize(request.Subjects);
            row.Version = Guid.NewGuid();
        }

        try
        {
            db.SaveChanges();
        }
        catch (DbUpdateException) when (RequestExists(request.RequestId))
        {
            // A concurrent writer inserted the same request id first — last write wins, and the filter confirms
            // the row is present, so this is the benign duplicate-key race. Any other DbUpdateException is a real
            // failure and propagates rather than being swallowed as a no-op.
        }
    }

    // Re-check on a fresh context whether the request row is present, to tell the expected duplicate-key race
    // from a genuine write failure.
    private bool RequestExists(string requestId)
    {
        using DbContext db = New();
        return db.Set<RequestEntity>().Find(requestId) is not null;
    }

    public void Resolve(string requestId, string instanceId)
    {
        // Removing one instance from the open list is a read-modify-write; the Version concurrency token makes a
        // racing resolve of the same request fail the optimistic check, so we reload and re-apply rather than
        // clobbering it (a stale last-write-wins would leave an instance "open" and the request lingering).
        while (true)
        {
            using DbContext db = New();
            RequestEntity? row = db.Set<RequestEntity>().Find(requestId);
            if (row is null)
            {
                return;
            }

            List<string> remaining = [.. Split(row.OpenInstanceIds).Where(i => i != instanceId)];
            if (remaining.Count == 0)
            {
                db.Set<RequestEntity>().Remove(row);
            }
            else
            {
                row.OpenInstanceIds = Join(remaining);
                row.Version = Guid.NewGuid();
            }

            try
            {
                db.SaveChanges();
                return;
            }
            catch (DbUpdateConcurrencyException)
            {
                // A concurrent resolve changed the row under us — loop to reload and re-apply the removal.
            }
        }
    }

    public void Close(string requestId)
    {
        using DbContext db = New();
        db.Set<RequestEntity>().Where(e => e.RequestId == requestId).ExecuteDelete();
    }

    public IReadOnlyCollection<OpenErasureRequest> Open()
    {
        using DbContext db = New();
        return [.. db.Set<RequestEntity>()
            .Select(e => new { e.RequestId, e.Deadline, e.OpenInstanceIds, e.Subjects })
            .AsEnumerable()
            .Select(e => new OpenErasureRequest(e.RequestId, e.Deadline, Split(e.OpenInstanceIds), Split(e.Subjects)))];
    }

    // ---- IPendingErasureRequests: the async front door's durable intake. The row carries only PII-free instance
    // ids resolved at admit, so no recoverable subject sits at rest. Admit is an idempotent upsert on the id.

    public void Admit(PendingErasureRequest request)
    {
        using DbContext db = New();
        PendingEntity? row = db.Set<PendingEntity>().Find(request.RequestId);
        if (row is null)
        {
            db.Set<PendingEntity>().Add(new PendingEntity
            {
                RequestId = request.RequestId,
                ReceivedAt = request.ReceivedAt,
                InstanceIds = Join(request.InstanceIds),
            });
        }
        else
        {
            row.ReceivedAt = request.ReceivedAt;
            row.InstanceIds = Join(request.InstanceIds);
        }

        try
        {
            db.SaveChanges();
        }
        catch (DbUpdateException) when (PendingExists(request.RequestId))
        {
            // A concurrent writer admitted the same request id first — the benign duplicate-key race; the filter
            // confirms the row is present. Any other DbUpdateException propagates rather than being swallowed.
        }
    }

    private bool PendingExists(string requestId)
    {
        using DbContext db = New();
        return db.Set<PendingEntity>().Find(requestId) is not null;
    }

    public IReadOnlyCollection<PendingErasureRequest> Pending()
    {
        using DbContext db = New();
        return [.. db.Set<PendingEntity>()
            .Select(e => new { e.RequestId, e.ReceivedAt, e.InstanceIds })
            .AsEnumerable()
            .Select(e => new PendingErasureRequest(e.RequestId, e.ReceivedAt, Split(e.InstanceIds)))];
    }

    public void Drained(string requestId)
    {
        using DbContext db = New();
        db.Set<PendingEntity>().Where(e => e.RequestId == requestId).ExecuteDelete();
    }

    public PendingBacklog Backlog()
    {
        // Read the admit times (a monitoring call); min in memory rather than relying on a provider-specific
        // DateTimeOffset aggregate translation.
        using DbContext db = New();
        List<DateTimeOffset> received = [.. db.Set<PendingEntity>().Select(e => e.ReceivedAt)];
        return new PendingBacklog(received.Count, received.Count == 0 ? null : received.Min());
    }

    private static string Join(IEnumerable<string> items) => string.Join(Sep, items);

    private string Tokenize(IEnumerable<string> subjects) => Join(subjects.Select(_protector.Tokenize));

    private static List<string> Split(string s) => string.IsNullOrEmpty(s) ? [] : [.. s.Split(Sep)];
}
