using System.Collections.Concurrent;

namespace PiiMaker.Access.Billing.Service;

/// <summary>
/// Billing state — a singleton on the access component's host ServiceCollection. A subscriber+period can be
/// marked to decline so the dunning path is demonstrable.
/// </summary>
public sealed class BillingStore
{
    public ConcurrentDictionary<(string, long), byte> Declines { get; } = new();
    public ConcurrentDictionary<(string, long), byte> Invoices { get; } = new();
    public ConcurrentDictionary<(string, long), int> Attempts { get; } = new();
}
