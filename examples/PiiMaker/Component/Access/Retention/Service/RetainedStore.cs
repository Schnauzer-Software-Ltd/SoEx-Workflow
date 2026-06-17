using System.Collections.Concurrent;

namespace PiiMaker.Access.Retention.Service;

/// <summary>
/// The retained-records store — a singleton on the access component's host ServiceCollection. The host
/// creates it, registers it, and keeps a reference to observe what was written outward. Idempotent on the
/// idempotency key (a re-driven OnRetaining writes once).
/// </summary>
public sealed class RetainedStore
{
    public ConcurrentDictionary<string, string> Records { get; } = new();
}
