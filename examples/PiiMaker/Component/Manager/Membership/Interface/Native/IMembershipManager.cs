namespace PiiMaker.Manager.Membership.Interface.Native;

public interface IMembershipManager
{
    /// <summary>Onboarding (flow A) — native single step.</summary>
    Task<StepReceipt> Onboard(OnboardCommand command);
    /// <summary>Subscription renew/dunning (flow B) — native single step.</summary>
    Task<StepReceipt> Renew(RenewCommand command);
    /// <summary>Offboarding (flow C) — native single step (the per-system revocation the flow fans out).</summary>
    Task<StepReceipt> Offboard(OffboardCommand command);
}