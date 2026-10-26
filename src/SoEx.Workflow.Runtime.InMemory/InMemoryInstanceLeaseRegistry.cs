using System.Collections.Concurrent;

namespace SoEx.Workflow.Runtime.InMemory;

/// <summary>
/// In-memory <see cref="IInstanceLeaseRegistry"/> — the instance leases in process memory. The Tier-1 default; a
/// deployment whose step workers and sweep run in different processes supplies a durable, shared implementation
/// instead. Concurrency-safe.
/// </summary>
public sealed class InMemoryInstanceLeaseRegistry : IInstanceLeaseRegistry
{
    private readonly ConcurrentDictionary<string, InstanceLease> _leases = new();

    public void Renew(InstanceLease lease) => _leases[lease.InstanceId] = lease;

    public void Forget(string instanceId) => _leases.TryRemove(instanceId, out _);

    public IReadOnlyCollection<InstanceLease> Leases() => [.. _leases.Values];
}
