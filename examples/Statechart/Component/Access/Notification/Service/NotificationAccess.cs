using StatechartDemo.Access.Notification.Interface;

namespace StatechartDemo.Access.Notification.Service;

/// <summary>
/// The demo's notifier: it records rather than sends. A real one would reach a mail or messaging system, and
/// nothing above it would change.
/// </summary>
public sealed class NotificationAccess : INotificationAccess
{
    private readonly List<string> _sent = [];

    public IReadOnlyList<string> Sent => _sent;

    public Task NotifyAsync(string claimId, string outcome, string? by = null)
    {
        _sent.Add(by is { Length: > 0 } who ? $"{claimId}: {outcome} by {who}" : $"{claimId}: {outcome}");
        return Task.CompletedTask;
    }
}
