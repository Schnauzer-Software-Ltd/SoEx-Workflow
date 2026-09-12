namespace PiiMaker.Manager.Membership.Interface;

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
/// What whoever accepts an invite can tell the flow at raise time. The flow cannot know this when it parks —
/// an invite may be accepted by someone other than the person it was addressed to — so it is supplied with the
/// raise and reaches the step as its second argument, rather than the raiser having to author the next step.
/// </summary>
public sealed record InviteAcceptance(string ConfirmedUser);
