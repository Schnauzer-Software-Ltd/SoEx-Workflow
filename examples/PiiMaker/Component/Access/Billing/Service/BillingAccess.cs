using System.Collections.Concurrent;
using PiiMaker.Access.Billing.Interface;

namespace PiiMaker.Access.Billing.Service;

/// <summary>Billing state — a singleton on the access component's host ServiceCollection. A subscriber+period
/// can be marked to decline so the dunning path is demonstrable.</summary>
public sealed class BillingStore
{
    public ConcurrentDictionary<(string, long), byte> Declines { get; } = new();
    public ConcurrentDictionary<(string, long), byte> Invoices { get; } = new();
    public ConcurrentDictionary<(string, long), int> Attempts { get; } = new();
}

/// <summary>In-memory <see cref="IBillingAccess"/> component. Charges succeed unless the (subscriber, period) is declined.</summary>
public sealed class BillingAccess(BillingStore store) : IBillingAccess
{
    public Task<bool> ChargeAsync(string subscriberId, long period)
    {
        store.Attempts.AddOrUpdate((subscriberId, period), 1, (_, n) => n + 1);
        return Task.FromResult(!store.Declines.ContainsKey((subscriberId, period)));
    }

    public Task InvoiceAsync(string subscriberId, long period)
    {
        store.Invoices.TryAdd((subscriberId, period), 0);
        return Task.CompletedTask;
    }
}
