using System.Collections.Concurrent;

namespace SoEx.Workflow.InMemory;

/// <summary>
/// In-memory <see cref="IHeldInstanceRegistry"/> — the quarantined-instance set in process memory. The Tier-1
/// default; a multi-process deployment that re-drives holds from a separately-hosted scheduler supplies a
/// durable, shared implementation instead. Concurrency-safe.
/// </summary>
public sealed class InMemoryHeldInstanceRegistry : IHeldInstanceRegistry
{
    private readonly ConcurrentDictionary<string, HeldInstance> _held = new();

    public void Record(HeldInstance held) => _held[held.InstanceId] = held;

    public void Clear(string instanceId) => _held.TryRemove(instanceId, out _);

    public IReadOnlyCollection<HeldInstance> Held() => [.. _held.Values];
}
