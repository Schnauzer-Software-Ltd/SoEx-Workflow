using SoEx.Workflow;
using Temporalio.Client;

namespace PiiMaker.Host.Temporal;

/// <summary>
/// The <see cref="IWorkflowGateway"/> that starts the consumer-authored native offboarding workflow (the
/// generic gateway would start the portable flow instead). Offboarding fans out and completes on its own,
/// so it has no continuation events.
/// </summary>
sealed class NativeOffboardGateway(ITemporalClient client, string taskQueue) : IWorkflowGateway
{
    public async Task StartAsync(string instanceId, byte[] sealedSeed)
    {
        byte[] seed = sealedSeed;   // a local for the expression-tree lambda
        await client.StartWorkflowAsync((NativeOffboardWorkflow wf) => wf.Run(seed),
            new WorkflowOptions(id: instanceId, taskQueue: taskQueue));
    }

    public Task RaiseEventAsync(string instanceId, string eventName, byte[]? sealedPayload = null, string? raiseId = null) =>
        throw new NotSupportedException("the offboarding fan-out completes on its own; it has no continuation events");
}
