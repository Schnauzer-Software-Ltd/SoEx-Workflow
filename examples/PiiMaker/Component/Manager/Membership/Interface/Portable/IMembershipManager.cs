using SoEx.Workflow;

namespace PiiMaker.Manager.Membership.Interface.Portable;

public interface IMembershipManager
{
    /// <summary>Onboarding (flow A) — portable.</summary>
    Task<WorkflowAction> Onboard(OnboardCommand command);
    /// <summary>Subscription renew/dunning (flow B) — portable.</summary>
    Task<WorkflowAction> Renew(RenewCommand command);
}