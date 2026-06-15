using Raven.Client.Documents;

namespace SoEx.Workflow.Maintenance.RavenDB;

/// <summary>
/// Durable <see cref="IHeldInstanceRegistry"/> over RavenDB compare-exchange — one entry per held instance, keyed
/// <c>{prefix}held/{instanceId}</c>. Survives a restart and is visible cross-process, so a separately-hosted
/// scheduler can re-drive quarantined instances across the fleet. Synchronous <c>Operations.Send</c>.
/// </summary>
public sealed class RavenDbHeldInstanceRegistry : IHeldInstanceRegistry
{
    private readonly IDocumentStore _store;
    private readonly string _prefix;

    public RavenDbHeldInstanceRegistry(IDocumentStore store, string prefix = "maint/")
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _prefix = prefix + "held/";
    }

    public void Record(HeldInstance held) => CompareExchangeOps.Upsert(_store, _prefix + held.InstanceId, Doc.From(held));

    public void Clear(string instanceId) => CompareExchangeOps.Delete<Doc>(_store, _prefix + instanceId);

    public IReadOnlyCollection<HeldInstance> Held() => [.. CompareExchangeOps.List<Doc>(_store, _prefix).Select(d => d.To())];

    /// <summary>The compare-exchange value (flat, so the serializer never has to round-trip the key struct).</summary>
    internal sealed class Doc
    {
        public string InstanceId { get; set; } = "";
        public string DtoType { get; set; } = "";
        public long Sequence { get; set; }
        public int Attempts { get; set; }
        public DateTimeOffset HeldAt { get; set; }
        public string? LastError { get; set; }

        public static Doc From(HeldInstance h) => new()
        {
            InstanceId = h.InstanceId,
            DtoType = h.IdempotencyKey.DtoType,
            Sequence = h.IdempotencyKey.Sequence,
            Attempts = h.Attempts,
            HeldAt = h.HeldAt,
            LastError = h.LastError,
        };

        public HeldInstance To() =>
            new(InstanceId, new IdempotencyKey(InstanceId, DtoType, Sequence), Attempts, HeldAt, LastError);
    }
}
