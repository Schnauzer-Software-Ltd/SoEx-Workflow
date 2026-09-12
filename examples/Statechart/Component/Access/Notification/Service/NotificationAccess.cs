using StatechartDemo.Access.Notification.Interface;

namespace StatechartDemo.Access.Notification.Service;

/// <summary>
/// The demo's notifier: it writes the notification out. A real one would reach a mail or messaging system,
/// and nothing above it would change — including the fact that it holds no state of its own.
/// </summary>
public sealed class NotificationAccess : INotificationAccess
{
    public Task NotifyAsync(string claimId, string outcome, string? by = null)
    {
        Console.WriteLine(by is { Length: > 0 } who
            ? $"   notified {claimId}: {outcome} by {who}"
            : $"   notified {claimId}: {outcome}");

        return Task.CompletedTask;
    }
}
