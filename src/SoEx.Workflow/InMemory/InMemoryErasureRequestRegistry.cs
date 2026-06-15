using System.Collections.Concurrent;

namespace SoEx.Workflow.InMemory;

/// <summary>
/// In-memory <see cref="IErasureRequestRegistry"/> — open erasure requests in process memory. The Tier-1 default;
/// a deployment that monitors deadlines from a separately-hosted scheduler supplies a durable, shared
/// implementation instead. Concurrency-safe; resolving the last open instance closes the request.
/// </summary>
public sealed class InMemoryErasureRequestRegistry : IErasureRequestRegistry
{
    private readonly ConcurrentDictionary<string, OpenErasureRequest> _open = new();

    public void Open(OpenErasureRequest request) => _open[request.RequestId] = request;

    public void Resolve(string requestId, string instanceId)
    {
        // Drop the resolved instance; close the request when none remain. No-op if the request is unknown.
        while (_open.TryGetValue(requestId, out OpenErasureRequest existing))
        {
            List<string> remaining = [.. existing.OpenInstanceIds.Where(i => i != instanceId)];
            if (remaining.Count == 0)
            {
                if (_open.TryRemove(new KeyValuePair<string, OpenErasureRequest>(requestId, existing)))
                {
                    return;
                }
            }
            else if (_open.TryUpdate(requestId, existing with { OpenInstanceIds = remaining }, existing))
            {
                return;
            }

            // a concurrent Resolve/Open changed it — re-read and retry
        }
    }

    public void Close(string requestId) => _open.TryRemove(requestId, out _);

    public IReadOnlyCollection<OpenErasureRequest> Open() => [.. _open.Values];
}
