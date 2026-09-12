namespace PiiMaker.Manager.Membership.Interface.Native;

public interface IMembershipManager
{
    /// <summary>Onboarding (flow A) — native single step. <paramref name="accepted"/> is non-null only on the
    /// step an "invite-accepted" raise carrying data resumed into.</summary>
    Task<StepReceipt> Onboard(OnboardCommand command, InviteAcceptance? accepted = null);
    /// <summary>Subscription renew/dunning (flow B) — native single step.</summary>
    Task<StepReceipt> Renew(RenewCommand command);
    /// <summary>Offboarding (flow C) — native single step (the per-system revocation the flow fans out).</summary>
    Task<StepReceipt> Offboard(OffboardCommand command);
}