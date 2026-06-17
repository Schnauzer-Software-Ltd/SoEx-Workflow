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

    /// <summary>Start onboarding (flow A) for an invitee.</summary>
    public sealed record StartOnboarding(string OrgId, string Email, string Offer) : TriggerBase;

    /// <summary>The identity provider confirmed the invitee verified their account.</summary>
    public sealed record AccountVerified(string OrgId, string Email) : TriggerBase;

    /// <summary>The invitee accepted the invite.</summary>
    public sealed record InviteAccepted(string OrgId, string Email) : TriggerBase;

    /// <summary>Start the renewal cycle (flow B) for a subscriber.</summary>
    public sealed record StartRenewal(string SubscriberId) : TriggerBase;

    /// <summary>The payment provider confirmed the subscriber updated their payment method.</summary>
    public sealed record PaymentUpdated(string SubscriberId) : TriggerBase;

    /// <summary>Start offboarding (flow C) for a leaver. Native-only fan-out — hosts whose runtime cannot fan
    /// out leave this flow unwired.</summary>
    public sealed record StartOffboarding(string SubjectId) : TriggerBase;
}
