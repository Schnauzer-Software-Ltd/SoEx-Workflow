using System.Collections.Concurrent;
using PiiMaker.Access.Retention.Interface;

namespace PiiMaker.Access.Retention.Service;

/// <summary>The retained-records store — a singleton on the access component's host ServiceCollection. The
/// host creates it, registers it, and keeps a reference to observe what was written outward. Idempotent on
/// the idempotency key (a re-driven OnRetaining writes once).</summary>
public sealed class RetainedStore
{
    public ConcurrentDictionary<string, string> Records { get; } = new();
}

/// <summary>In-memory <see cref="IRetainedRecordAccess"/> component — writes the must-retain record to the injected store.</summary>
public sealed class RetainedRecordAccess(RetainedStore store) : IRetainedRecordAccess
{
    public Task RetainAsync(string idempotencyKey, string record)
    {
        store.Records[idempotencyKey] = record;
        return Task.CompletedTask;
    }
}
