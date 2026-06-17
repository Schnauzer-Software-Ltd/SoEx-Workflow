using System.Collections.Concurrent;

namespace PiiMaker.Engine.Subscription.Service;

/// <summary>Subscription state — a singleton on the engine component's host ServiceCollection.</summary>
public sealed class SubscriptionStore
{
    public int Reservations;
    public ConcurrentDictionary<string, string> Assigned { get; } = new();
    public ConcurrentDictionary<string, byte> Released { get; } = new();
    public ConcurrentDictionary<string, string> Cancelled { get; } = new();
}
