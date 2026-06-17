using SoEx.Workflow;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;

namespace SoEx.Workflow.Runtime.Temporal;

/// <summary>
/// Test host — runs the portable flow on Temporal's time-skipping test environment (Tier-1; idle durable
/// timers are skipped instantly, no external server). One environment per run, one workflow to completion.
/// For durable hosting against a live cluster use <see cref="TemporalWorkflowHost"/>. Use this or the native
/// flow, never both in one host.
/// </summary>
public sealed class TemporalTestWorkflowHost(IGovernedStep step, GovernedTermination termination)
{
    private const string TaskQueue = "soex-workflow";

    public async Task<byte[]> RunAsync(string instanceId, byte[] seed, IReadOnlyDictionary<string, byte[]>? prearmedEvents)
    {
        // The instance id and event names are journaled in clear, so they must not carry the subject.
        byte[]? ambient = step.AmbientOf(instanceId, seed);
        step.GuardVisibleName(instanceId, ambient);
        if (prearmedEvents is not null)
        {
            foreach (string name in prearmedEvents.Keys)
            {
                step.GuardVisibleName(name, ambient);
            }
        }

        await using WorkflowEnvironment env = await WorkflowEnvironment.StartTimeSkippingAsync();

        using var worker = TemporalWorkflowHost.BuildWorker(env.Client, TaskQueue, step, termination);

        return await worker.ExecuteAsync(async () =>
        {
            WorkflowHandle<WorkflowOrchestration, byte[]> handle = await env.Client.StartWorkflowAsync(
                (WorkflowOrchestration wf) => wf.Run(seed),
                new WorkflowOptions(id: instanceId, taskQueue: TaskQueue));

            if (prearmedEvents is not null)
            {
                foreach ((string name, byte[] payload) in prearmedEvents)
                {
                    await handle.SignalAsync(wf => wf.RaiseEvent(name, payload, null));
                }
            }

            return await handle.GetResultAsync();
        });
    }
}
