namespace PiiMaker.Manager.Membership.Interface;

/// <summary>
/// The inbound triggers a caller can fire at the Membership manager (see
/// <see cref="IMembershipManager.Trigger"/>): one closed set, one case per trigger, each carrying only
/// business identity — no instance handle, no flow knowledge. Start triggers begin a flow; the others signal
/// a business event into a running one. Sent over the wire polymorphically: the controller binds the base and
/// the <c>$type</c> discriminator names the case.
/// </summary>
public abstract record TriggerBase
{
    private TriggerBase() { }

    /// <summary>Start onboarding (flow A) for an invitee. <paramref name="Attempt"/> folds into the derived
    /// instance id, so bumping it starts a genuinely new run for the same org+email once an earlier run exists
    /// (the identity alone is one-flow-per-subject by design). Defaults to 0 — the first/only attempt.</summary>
    public sealed record StartOnboarding(string OrgId, string Email, string Offer, int Attempt = 0) : TriggerBase;

    /// <summary>The identity provider confirmed the invitee verified their account. Carries the same
    /// <paramref name="Attempt"/> as the start it belongs to, so the event routes to that run's instance.</summary>
    public sealed record AccountVerified(string OrgId, string Email, int Attempt = 0) : TriggerBase;

    /// <summary>The invitee accepted the invite. Carries the same <paramref name="Attempt"/> as its start so the
    /// event routes to that run's instance.</summary>
    public sealed record InviteAccepted(string OrgId, string Email, int Attempt = 0) : TriggerBase;

    /// <summary>Start the renewal cycle (flow B) for a subscriber.</summary>
    public sealed record StartRenewal(string SubscriberId) : TriggerBase;

    /// <summary>The payment provider confirmed the subscriber updated their payment method.</summary>
    public sealed record PaymentUpdated(string SubscriberId) : TriggerBase;

    /// <summary>The subscriber asked to cancel. A renewal parked in dunning can be resumed by this as well as
    /// by <see cref="PaymentUpdated"/>, and the two mean opposite things, so they are separate events rather
    /// than one event told apart by its payload.</summary>
    public sealed record CancellationRequested(string SubscriberId) : TriggerBase;

    /// <summary>Start offboarding (flow C) for a leaver. Native-only fan-out — hosts whose runtime cannot fan
    /// out leave this flow unwired.</summary>
    public sealed record StartOffboarding(string SubjectId) : TriggerBase;
}
