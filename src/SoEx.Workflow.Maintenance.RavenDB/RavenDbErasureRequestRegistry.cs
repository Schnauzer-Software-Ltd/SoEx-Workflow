using Raven.Client.Documents;

namespace SoEx.Workflow.Maintenance.RavenDB;

/// <summary>
/// Durable <see cref="IErasureRequestRegistry"/> over RavenDB compare-exchange — one entry per open request, keyed
/// <c>{prefix}req/{requestId}</c>. Survives a restart and is visible cross-process, so a separately-hosted
/// scheduler can monitor statutory deadlines across the fleet. Synchronous <c>Operations.Send</c>.
/// <para>
/// The subjects of a person actively exercising erasure are <b>tokenized</b> via the <see cref="ISubjectProtector"/>
/// before the compare-exchange value is written, so the stored document holds only the same one-way, PII-free token
/// a durable <see cref="ISubjectIndex"/> uses — never a recoverable subject at rest (which RavenDB tombstones,
/// revisions and backups would otherwise outlive the request). The registry never needs the plaintext back (deadline
/// review routes by instance id), so a one-way token suffices.
/// </para>
/// </summary>
public sealed class RavenDbErasureRequestRegistry : IErasureRequestRegistry, IPendingErasureRequests
{
    private readonly IDocumentStore _store;
    private readonly string _prefix;
    private readonly string _pendingPrefix;
    private readonly ISubjectProtector _protector;

    /// <param name="protector">Derives the PII-free at-rest token for each subject (never the plaintext). Required:
    /// a durable registry must not persist a recoverable subject of someone exercising erasure. Use the same
    /// <see cref="HmacSubjectProtector"/> the durable subject index uses.</param>
    public RavenDbErasureRequestRegistry(IDocumentStore store, ISubjectProtector protector, string prefix = "maint/")
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _protector = DurableSubjectProtector.Require(protector, "a durable erasure-request registry");
        _prefix = prefix + "req/";
        _pendingPrefix = prefix + "pending/";
    }

    public void Open(OpenErasureRequest request) => CompareExchangeOps.Upsert(_store, _prefix + request.RequestId, Doc.From(request, _protector));

    public void Resolve(string requestId, string instanceId) =>
        // Atomic read-modify-write: removing one instance from the open list is re-applied against the current
        // value under compare-exchange, so concurrent per-instance resolves of one request don't lose updates
        // (a stale read-then-write would leave an instance "open" and the request lingering). Empty ⇒ delete.
        CompareExchangeOps.Mutate<Doc>(_store, _prefix + requestId, doc =>
        {
            doc.OpenInstanceIds = [.. doc.OpenInstanceIds.Where(i => i != instanceId)];
            return doc.OpenInstanceIds.Count == 0 ? null : doc;
        });

    public void Close(string requestId) => CompareExchangeOps.Delete<Doc>(_store, _prefix + requestId);

    public IReadOnlyCollection<OpenErasureRequest> Open() => [.. CompareExchangeOps.List<Doc>(_store, _prefix).Select(d => d.To())];

    // ---- IPendingErasureRequests: the async front door's durable intake, keyed {prefix}pending/{requestId}.
    // The pending record carries only PII-free instance ids (resolved from the index at admit), so no recoverable
    // subject sits at rest here either — no protector needed. Admit is an idempotent upsert on the request id.

    public void Admit(PendingErasureRequest request) =>
        CompareExchangeOps.Upsert(_store, _pendingPrefix + request.RequestId, PendingDoc.From(request));

    public IReadOnlyCollection<PendingErasureRequest> Pending() =>
        [.. CompareExchangeOps.List<PendingDoc>(_store, _pendingPrefix).Select(d => d.To())];

    public void Drained(string requestId) => CompareExchangeOps.Delete<PendingDoc>(_store, _pendingPrefix + requestId);

    public PendingBacklog Backlog()
    {
        // A monitoring read (not the hot admit path), so listing the pending set to count + find the oldest is fine.
        List<PendingDoc> docs = [.. CompareExchangeOps.List<PendingDoc>(_store, _pendingPrefix)];
        return new PendingBacklog(docs.Count, docs.Count == 0 ? null : docs.Min(d => d.ReceivedAt));
    }

    /// <summary>The compare-exchange value.</summary>
    internal sealed class Doc
    {
        public string RequestId { get; set; } = "";
        public DateTimeOffset Deadline { get; set; }
        public List<string> OpenInstanceIds { get; set; } = [];

        /// <summary>The request's subjects as one-way <see cref="ISubjectProtector"/> tokens — never recoverable
        /// plaintext (the registry routes deadline review by instance id, so it never needs the subject back).</summary>
        public List<string> Subjects { get; set; } = [];

        public static Doc From(OpenErasureRequest r, ISubjectProtector protector) => new()
        {
            RequestId = r.RequestId,
            Deadline = r.Deadline,
            OpenInstanceIds = [.. r.OpenInstanceIds],
            Subjects = [.. r.Subjects.Select(protector.Tokenize)], // one-way tokens, never recoverable plaintext at rest
        };

        public OpenErasureRequest To() => new(RequestId, Deadline, OpenInstanceIds, Subjects);
    }

    /// <summary>The pending-intake compare-exchange value — PII-free instance ids only.</summary>
    internal sealed class PendingDoc
    {
        public string RequestId { get; set; } = "";
        public DateTimeOffset ReceivedAt { get; set; }
        public List<string> InstanceIds { get; set; } = [];

        public static PendingDoc From(PendingErasureRequest r) => new()
        {
            RequestId = r.RequestId,
            ReceivedAt = r.ReceivedAt,
            InstanceIds = [.. r.InstanceIds],
        };

        public PendingErasureRequest To() => new(RequestId, ReceivedAt, InstanceIds);
    }
}
