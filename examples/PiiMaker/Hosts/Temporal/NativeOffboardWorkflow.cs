using Temporalio.Workflows;
using Wf = Temporalio.Workflows.Workflow;

namespace PiiMaker.Host.Temporal;

/// <summary>
/// The consumer's native offboarding flow (C): a Temporal <c>[Workflow]</c> that fans out governed
/// revocations across every downstream system in parallel. The GovernedTerminationInterceptor runs the
/// termination shred when the workflow completes.
/// </summary>
[Workflow]
public class NativeOffboardWorkflow
{
    [WorkflowRun]
    public async Task<string> Run(byte[] seed)
    {
        var options = new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) };
        string[] systems = ["mail", "vpn", "billing-portal", "wiki"];

        var revocations = new List<Task>();
        for (int i = 0; i < systems.Length; i++)
        {
            string system = systems[i];
            long seq = i;
            revocations.Add(Wf.ExecuteActivityAsync((GovernedOffboard a) => a.Revoke(seed, system, seq), options));
        }

        await Task.WhenAll(revocations);
        return "offboarded";
    }
}
