using PiiMaker.Hosting;
using PiiMaker.Manager.Membership.Interface;
using Native = PiiMaker.Manager.Membership.Interface.Native;
using SoEx.Workflow;
using Temporalio.Activities;

namespace PiiMaker.Host.Temporal;

/// <summary>One governed revocation per downstream system; the subject is recovered through the framework.</summary>
sealed class GovernedOffboard(GovernedStep<Native.IMembershipManager> step)
{
    [Activity]
    public Task<StepReceipt> Revoke(byte[] seed, string system, long seq) =>
        MembershipNative.RunOffboardStep(step, ActivityExecutionContext.Current.Info.WorkflowId!, seq, system, seed);
}
