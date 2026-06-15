using System.Collections.Concurrent;

namespace SoEx.Workflow.InMemory;

/// <summary>
/// In-memory <see cref="IPendingErasureRequests"/> — admitted erasure requests in process memory. The Tier-1
/// default; a deployment that must survive a crash between admit and drain, or drain from a separate worker,
/// supplies a durable, shared implementation instead. Admit is idempotent on the request id (a duplicate
/// "forget subject S" collapses to one pending request); draining removes it.
/// </summary>
public sealed class InMemoryPendingErasureRequests : IPendingErasureRequests
{
    private readonly ConcurrentDictionary<string, PendingErasureRequest> _pending = new();

    public void Admit(PendingErasureRequest request) => _pending[request.RequestId] = request;

    public IReadOnlyCollection<PendingErasureRequest> Pending() => [.. _pending.Values];

    public void Drained(string requestId) => _pending.TryRemove(requestId, out _);

    public PendingBacklog Backlog()
    {
        PendingErasureRequest[] snapshot = [.. _pending.Values];
        return new PendingBacklog(snapshot.Length, snapshot.Length == 0 ? null : snapshot.Min(p => p.ReceivedAt));
    }
}
