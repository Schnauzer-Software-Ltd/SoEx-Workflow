using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Memory;

namespace PiiMaker.Host.Elsa;

/// <summary>
/// The native Elsa onboarding flow as a REGISTERED workflow, so a fresh host rebuilds the identical
/// definition and resumes the persisted bookmark: Lookup → Reserve → SendInvite → wait(invite-accepted) →
/// Assign → termination. Governance is anchored on the correlation id (which the host controls and sealed
/// the seed under).
/// </summary>
public class MembershipOnboardWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder) => builder.Root = new Sequence
    {
        // the sealed seed's persisted home across suspensions (GovStep copies it from the start input);
        // workflow storage, not the default memory driver — it must survive a host restart
        Variables = { new Variable<string>("seed", "").WithWorkflowStorage() },
        Activities =
        {
            new GovStep { Kind = "lookup", Seq = 0 },
            new GovStep { Kind = "reserve", Seq = 1 },
            new GovStep { Kind = "invite", Seq = 2 },
            new WaitEvent { EventName = "invite-accepted" },
            new GovStep { Kind = "assign", Seq = 3 },
            new GovTermination(),
        },
    };
}
