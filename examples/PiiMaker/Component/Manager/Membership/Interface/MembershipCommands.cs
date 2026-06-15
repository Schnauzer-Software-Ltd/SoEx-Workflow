namespace PiiMaker.Manager.Membership.Interface;

/// <summary>
/// The PII-free result a native step returns. It names the step (a kind, never a subject value) and an
/// optional non-PII detail (e.g. a reservation id). It is journaled in clear and survives the shred, so it
/// must carry no subject — the framework enforces this.
/// </summary>
public sealed record StepReceipt(string Step, string? Detail = null);

/// <summary>
/// Onboarding (flow A) commands. Each carries the saga state the next step needs (org, offer, reservation
/// id) so the portable flow threads it through the sealed envelope; the subject (email) rides the commands
/// and the encrypted payload, never a runtime-visible name.
/// </summary>
public abstract record OnboardCommand
{
    private OnboardCommand() { }

    public sealed record LookupUser(string OrgId, string Email, string Offer) : OnboardCommand;
    public sealed record CreateAccount(string OrgId, string Email, string Offer) : OnboardCommand;
    public sealed record ReserveSubscription(string OrgId, string Email, string Offer) : OnboardCommand;
    public sealed record SendInvite(string OrgId, string Email, string Offer, string ReservationId) : OnboardCommand;
    public sealed record AssignSubscription(string ReservationId, string ConfirmedUser) : OnboardCommand;
    public sealed record ReleaseReservation(string ReservationId) : OnboardCommand;
    public sealed record Abandon(string Reason) : OnboardCommand;
}

/// <summary>
/// Subscription renewal/dunning (flow B) commands. The renewal cycle continues-as-new across periods; a
/// declined charge enters dunning. The subscriber id is the subject.
/// </summary>
public abstract record RenewCommand
{
    private RenewCommand() { }

    public sealed record Charge(string SubscriberId, int Period) : RenewCommand;
    public sealed record Dun(string SubscriberId, int Period, int Attempt) : RenewCommand;
    public sealed record Cancel(string SubscriberId, string Reason) : RenewCommand;
}

/// <summary>
/// Offboarding (flow C) commands. The native flow fans these out — one revocation per downstream system —
/// then archives the employment record at termination via <c>OnRetaining</c>. The leaver is the subject.
/// </summary>
public abstract record OffboardCommand
{
    private OffboardCommand() { }

    public sealed record Revoke(string SubjectId, string System) : OffboardCommand;
}
