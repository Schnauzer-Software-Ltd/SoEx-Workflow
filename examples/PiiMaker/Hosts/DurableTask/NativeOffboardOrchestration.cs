using Microsoft.DurableTask;
using PiiMaker.Manager.Membership.Interface;
using SoEx.Workflow.Runtime.DurableTask;

namespace PiiMaker.Host.DurableTask;

/// <summary>
/// C — offboarding: revoke access across every downstream system in parallel; the subject rides sealed and
/// is recovered per revocation. The base orchestrator's termination hook shreds when the fan-out completes.
/// </summary>
public sealed class NativeOffboardOrchestration : GovernedTaskOrchestrator<byte[], string>
{
    protected override async Task<string> Flow(TaskOrchestrationContext context, byte[] seed)
    {
        string id = context.InstanceId;
        string[] systems = ["mail", "vpn", "billing-portal", "wiki"];

        var revocations = new List<Task>();
        for (int i = 0; i < systems.Length; i++)
        {
            revocations.Add(context.CallActivityAsync<StepReceipt>("Revoke", new RevokeInput(seed, id, i, systems[i])));
        }

        await Task.WhenAll(revocations);
        return "offboarded";
    }
}
