using System.Collections.Concurrent;

namespace SoEx.Workflow.Runtime.InMemory;

/// <summary>
/// In-memory <see cref="IErasureTombstone"/>: an append-only set of erased instance ids. The reference and
/// Tier-1 default; production needs a durable, shared implementation so the tombstone survives the process and
/// keeps a raise-after-shred from resurrecting the flow across a restart.
/// </summary>
public sealed class InMemoryErasureTombstone : IErasureTombstone
{
    private readonly ConcurrentDictionary<string, byte> _erased = new();

    public void Record(string instanceId) => _erased[instanceId] = 0;

    public bool IsErased(string instanceId) => _erased.ContainsKey(instanceId);
}
