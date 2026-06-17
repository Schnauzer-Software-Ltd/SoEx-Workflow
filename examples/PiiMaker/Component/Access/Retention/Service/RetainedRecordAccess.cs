using PiiMaker.Access.Retention.Interface;

namespace PiiMaker.Access.Retention.Service;

/// <summary>In-memory <see cref="IRetainedRecordAccess"/> component — writes the must-retain record to the injected store.</summary>
public sealed class RetainedRecordAccess(RetainedStore store) : IRetainedRecordAccess
{
    public Task RetainAsync(string idempotencyKey, string record)
    {
        store.Records[idempotencyKey] = record;
        return Task.CompletedTask;
    }
}
