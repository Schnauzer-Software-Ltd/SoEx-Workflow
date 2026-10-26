using Raven.Client.Documents;

namespace SoEx.Workflow.Maintenance.RavenDB;

/// <summary>
/// Durable <see cref="IInstanceLeaseRegistry"/> over RavenDB compare-exchange — one entry per leased instance, keyed
/// <c>{prefix}lease/{instanceId}</c>. Survives a restart and is visible cross-process, so the step workers that renew
/// and the sweep that reads can run in different processes. Synchronous <c>Operations.Send</c>.
/// </summary>
public sealed class RavenDbInstanceLeaseRegistry : IInstanceLeaseRegistry
{
    private readonly IDocumentStore _store;
    private readonly string _prefix;

    public RavenDbInstanceLeaseRegistry(IDocumentStore store, string prefix = "maint/")
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _prefix = prefix + "lease/";
    }

    public void Renew(InstanceLease lease) => CompareExchangeOps.Upsert(_store, _prefix + lease.InstanceId, Doc.From(lease));

    public void Forget(string instanceId) => CompareExchangeOps.Delete<Doc>(_store, _prefix + instanceId);

    public IReadOnlyCollection<InstanceLease> Leases() => [.. CompareExchangeOps.List<Doc>(_store, _prefix).Select(d => d.To())];

    /// <summary>The compare-exchange value.</summary>
    internal sealed class Doc
    {
        public string InstanceId { get; set; } = "";
        public DateTimeOffset RenewedAt { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }

        public static Doc From(InstanceLease l) => new() { InstanceId = l.InstanceId, RenewedAt = l.RenewedAt, ExpiresAt = l.ExpiresAt };

        public InstanceLease To() => new(InstanceId, RenewedAt, ExpiresAt);
    }
}
