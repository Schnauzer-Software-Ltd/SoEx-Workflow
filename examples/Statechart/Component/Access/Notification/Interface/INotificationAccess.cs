namespace StatechartDemo.Access.Notification.Interface;

/// <summary>
/// Resource-access for telling someone what happened to their expense claim. A SoEx component contract;
/// Task-returning. Idempotent on the claim and the outcome, because a redelivered step re-runs it.
/// </summary>
public interface INotificationAccess
{
    /// <summary>Notifies the claimant of an outcome ("approved", "rejected", "escalated").</summary>
    Task NotifyAsync(string claimId, string outcome, string? by = null);

    /// <summary>What has been sent, for the demo to print. A real access component would not expose this.</summary>
    IReadOnlyList<string> Sent { get; }
}
