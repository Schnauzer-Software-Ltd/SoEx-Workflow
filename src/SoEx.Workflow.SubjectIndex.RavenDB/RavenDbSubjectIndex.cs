using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace SoEx.Workflow.SubjectIndex.RavenDB;

/// <summary>
/// A durable, shared <see cref="ISubjectIndex"/> over RavenDB. The bidirectional subject↔instance edge set is
/// held as two documents per key — a subject document listing its instances and an instance document listing
/// its subjects — so lookups are strongly-consistent point <c>Load</c>s (no eventually-consistent query) and
/// the edges survive a restart and are visible cross-process. RavenDB is the single source of truth, so a
/// right-to-erasure request reaching any node finds every instance touching the subject.
/// <para>
/// Mutations use optimistic concurrency with a bounded retry, since concurrent governed steps may add edges
/// to the same subject; reads return snapshots. Synchronous RavenDB sessions throughout (no async bridge).
/// </para>
/// </summary>
public sealed class RavenDbSubjectIndex : ISubjectIndex
{
    private const int MaxRetries = 100;

    private readonly IDocumentStore _store;
    private readonly string _prefix;
    private readonly ISubjectProtector _protector;
    private readonly IInstanceKeyStore _keys;

    /// <param name="store">A connected RavenDB document store (the shared, durable backing store).</param>
    /// <param name="protector">Derives the PII-free at-rest lookup token for a subject (the subject-doc id and the
    /// instance-doc map key) — never the plaintext subject.</param>
    /// <param name="keys">The per-instance crypto-shred key store (the same one the instances' data uses). Each
    /// subject is sealed under its instance's key, so a document holds only a one-way token and a blob that
    /// becomes unrecoverable when that instance is crypto-shredded at termination. Required: a durable index must
    /// not persist recoverable plaintext PII.</param>
    /// <param name="prefix">Document-id prefix isolating this index's documents.</param>
    public RavenDbSubjectIndex(IDocumentStore store, ISubjectProtector protector, IInstanceKeyStore keys, string prefix = "subjidx/")
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(keys);
        _store = store;
        _protector = DurableSubjectProtector.Require(protector, "a durable subject index");
        _keys = keys;
        _prefix = prefix;
    }

    public void AddEdge(string subjectId, string instanceId) => InTransaction(session =>
    {
        string token = _protector.Tokenize(subjectId);
        SubjectDoc subject = LoadOrCreate<SubjectDoc>(session, SubjectKey(token));
        subject.Instances.Add(instanceId);

        // Seal the subject under the INSTANCE's per-instance key, so the edge inherits that instance's crypto-shred.
        InstanceDoc instance = LoadOrCreate<InstanceDoc>(session, InstanceKey(instanceId));
        instance.Subjects[token] = _keys.Encrypt(instanceId, System.Text.Encoding.UTF8.GetBytes(subjectId));
    });

    public void RemoveInstance(string instanceId) => InTransaction(session =>
    {
        var instance = session.Load<InstanceDoc>(InstanceKey(instanceId));
        if (instance is null)
        {
            return;
        }

        foreach (string token in instance.Subjects.Keys)
        {
            var subject = session.Load<SubjectDoc>(SubjectKey(token));
            if (subject is null)
            {
                continue;
            }

            subject.Instances.Remove(instanceId);
            if (subject.Instances.Count == 0)
            {
                session.Delete(subject); // prune empty subject docs — lifetime equals the instances'.
            }
        }

        session.Delete(instance);
    });

    public IReadOnlyCollection<string> InstancesFor(string subjectId)
    {
        using IDocumentSession session = _store.OpenSession();
        return session.Load<SubjectDoc>(SubjectKey(_protector.Tokenize(subjectId))) is { } doc ? [.. doc.Instances] : [];
    }

    public IReadOnlyCollection<string> SubjectsFor(string instanceId)
    {
        using IDocumentSession session = _store.OpenSession();
        if (session.Load<InstanceDoc>(InstanceKey(instanceId)) is not { } doc)
        {
            return [];
        }

        var subjects = new List<string>(doc.Subjects.Count);
        foreach (byte[] blob in doc.Subjects.Values)
        {
            // Open under the instance's key. If the key is gone (the instance was crypto-shredded), the subject is
            // unrecoverable — skip it rather than surface a half-shredded edge.
            try { subjects.Add(System.Text.Encoding.UTF8.GetString(_keys.Decrypt(instanceId, blob))); }
            catch (InvalidOperationException) { }
        }

        return subjects;
    }

    private void InTransaction(Action<IDocumentSession> work)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using IDocumentSession session = _store.OpenSession();
                session.Advanced.UseOptimisticConcurrency = true; // a racing edge-add on the same doc → retry, not clobber.
                work(session);
                session.SaveChanges();
                return;
            }
            catch (ConcurrencyException) when (attempt < MaxRetries)
            {
                // Another writer changed a document we touched between our load and save. Back off with jitter
                // before re-reading: a hot document (one instance touched by many subjects, or one subject by
                // many instances) draws concurrent edge-adds, and a tight immediate-retry spin would thunder —
                // every loser retrying in lockstep collides again. Jittered backoff disperses them so each lands.
                Backoff(attempt);
            }
        }
    }

    // Exponential backoff capped at ~25ms, fully jittered (sleep a random slice up to the cap) so concurrent
    // retriers spread out instead of re-colliding. Random.Shared is thread-safe.
    private static void Backoff(int attempt)
    {
        int capMs = Math.Min(25, 1 << Math.Min(attempt, 4));
        Thread.Sleep(Random.Shared.Next(1, capMs + 1));
    }

    private static T LoadOrCreate<T>(IDocumentSession session, string id) where T : class, new()
    {
        T? doc = session.Load<T>(id);
        if (doc is null)
        {
            doc = new T();
            session.Store(doc, id);
        }

        return doc;
    }

    private string SubjectKey(string subjectToken) => _prefix + "s/" + subjectToken;

    private string InstanceKey(string instanceId) => _prefix + "i/" + instanceId;

    /// <summary>A subject's instances, keyed in the document id by the subject's one-way token.</summary>
    internal sealed class SubjectDoc
    {
        public HashSet<string> Instances { get; set; } = [];
    }

    /// <summary>An instance's subjects as token → sealed-blob (drives termination pruning and SubjectsFor recovery).</summary>
    internal sealed class InstanceDoc
    {
        public Dictionary<string, byte[]> Subjects { get; set; } = [];
    }
}
