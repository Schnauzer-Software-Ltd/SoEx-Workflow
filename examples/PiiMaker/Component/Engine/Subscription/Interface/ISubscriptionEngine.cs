namespace PiiMaker.Engine.Subscription.Interface;

/// <summary>
/// Engine-role component owning subscription lifecycle state. A SoEx component contract — all
/// operations are Task-returning. Identifiers it handles (org, offer, reservation id) are non-subject.
/// </summary>
public interface ISubscriptionEngine
{
    /// <summary>Reserves a seat for an offer; returns a (non-PII) reservation id.</summary>
    Task<string> ReserveAsync(string orgId, string offer);

    /// <summary>Assigns a reserved seat to the confirmed user (idempotent on the reservation id).</summary>
    Task AssignAsync(string reservationId, string user);

    /// <summary>Releases a reservation (compensation when an invite expires).</summary>
    Task ReleaseAsync(string reservationId);

    /// <summary>Cancels the subscription (dunning exhausted / member-requested).</summary>
    Task CancelAsync(string subscriberId, string reason);
}
