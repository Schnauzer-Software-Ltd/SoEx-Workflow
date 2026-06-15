using System.Collections.Concurrent;

namespace SoEx.Workflow.InMemory;

/// <summary>
/// In-memory <see cref="IIdempotencyStore"/> — records applied keys and their results in process
/// memory. The effect runs <b>exactly once</b> per key even under concurrent delivery (a
/// <see cref="Lazy{T}"/> gate); a failed effect is not recorded, so a redelivery re-runs it.
/// In-process reference implementation; production supplies a durable store.
/// </summary>
public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<IdempotencyKey, Lazy<Task<byte[]>>> _applied = new();

    public async Task<byte[]> ApplyOnceAsync(IdempotencyKey key, Func<Task<byte[]>> effect)
    {
        Lazy<Task<byte[]>> gate = _applied.GetOrAdd(
            key, _ => new Lazy<Task<byte[]>>(effect, LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await gate.Value;
        }
        catch
        {
            // A failed effect leaves no record — the step did not complete, so a redelivery must retry.
            _applied.TryRemove(KeyValuePair.Create(key, gate));
            throw;
        }
    }
}
