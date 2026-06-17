using PiiMaker.Engine.Subscription.Interface;

namespace PiiMaker.Engine.Subscription.Service;

/// <summary>In-memory <see cref="ISubscriptionEngine"/> component — records reservations/assignments via the injected store.</summary>
public sealed class SubscriptionEngine(SubscriptionStore store) : ISubscriptionEngine
{
    public Task<string> ReserveAsync(string orgId, string offer) =>
        Task.FromResult($"res-{Interlocked.Increment(ref store.Reservations)}");

    public Task AssignAsync(string reservationId, string user)
    {
        store.Assigned[reservationId] = user;
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(string reservationId)
    {
        store.Released.TryAdd(reservationId, 0);
        return Task.CompletedTask;
    }

    public Task CancelAsync(string subscriberId, string reason)
    {
        store.Cancelled[subscriberId] = reason;
        return Task.CompletedTask;
    }
}
