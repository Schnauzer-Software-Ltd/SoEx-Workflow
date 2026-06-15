using SoEx.Workflow;

namespace PiiMaker.Manager.Membership.Interface;

/// <summary>
/// The one Membership entrypoint. Each demo flow is a distinct operation; a host binds the one it wants by
/// name (<c>GovernedStep</c> operation selection), so a single component models several flows.
/// <para>
/// Two shapes per flow that supports both consumption models: a <b>portable</b> operation returns a
/// <see cref="WorkflowAction"/> (the component <i>is</i> the flow; the generic driver drives it on any
/// runtime); a <b>native</b> operation returns a PII-free <see cref="StepReceipt"/> (the backend owns the
/// flow and calls the operation per step). Offboarding is native-only — its fan-out can't be expressed by
/// the sequential portable flow.
/// </para>
/// </summary>
public interface IMembershipManager
{
    /// <summary>Onboarding (flow A) — portable.</summary>
    Task<WorkflowAction> Onboard(OnboardCommand command);

    /// <summary>Onboarding (flow A) — native single step.</summary>
    Task<StepReceipt> OnboardStep(OnboardCommand command);

    /// <summary>Subscription renew/dunning (flow B) — portable.</summary>
    Task<WorkflowAction> Renew(RenewCommand command);

    /// <summary>Subscription renew/dunning (flow B) — native single step.</summary>
    Task<StepReceipt> RenewStep(RenewCommand command);

    /// <summary>Offboarding (flow C) — native single step (the per-system revocation the flow fans out).</summary>
    Task<StepReceipt> OffboardStep(OffboardCommand command);
}

/// <summary>Tunable policy for the Membership flows — TTLs, renewal cadence, dunning limits.</summary>
public sealed record MembershipPolicy(
    TimeSpan AccountTtl,
    TimeSpan InviteTtl,
    TimeSpan RenewalInterval,
    TimeSpan DunningBackoff,
    int RenewalPeriods,
    int MaxDunningAttempts)
{
    /// <summary>Demo-friendly defaults.</summary>
    public static MembershipPolicy Default { get; } = new(
        AccountTtl: TimeSpan.FromHours(24),
        InviteTtl: TimeSpan.FromDays(7),
        RenewalInterval: TimeSpan.FromDays(30),
        DunningBackoff: TimeSpan.FromDays(2),
        RenewalPeriods: 3,
        MaxDunningAttempts: 3);
}
