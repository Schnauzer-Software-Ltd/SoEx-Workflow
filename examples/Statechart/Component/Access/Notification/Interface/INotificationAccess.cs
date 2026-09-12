namespace StatechartDemo.Access.Notification.Interface;

/// <summary>
/// Resource-access for telling a claimant what happened to their expense claim. A SoEx component contract;
/// Task-returning. Idempotent on the claim and the outcome, because a redelivered step re-runs it.
/// <para>
/// It emits and keeps nothing. A SoEx component is resolved per call, so an instance never outlives the
/// invocation that created it — anything a component accumulated in a field would be gone by the next step,
/// and a contract that offered to report it back would be lying about its own lifetime. State that has to
/// survive belongs in the sealed journal or in an injected store, never in the component.
/// </para>
/// </summary>
public interface INotificationAccess
{
    /// <summary>Notifies the claimant of an outcome ("approved", "rejected", "escalated").</summary>
    Task NotifyAsync(string claimId, string outcome, string? by = null);
}
