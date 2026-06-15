using Temporalio.Client;

namespace SoEx.Workflow.Temporal;

/// <summary>
/// The <see cref="IWorkflowGateway"/> over a Temporal client + task queue. Start submits the
/// portable flow's workflow with the sealed seed; Raise signals the instance's
/// <c>RaiseEvent(name, payload)</c> handler. A consumer-authored native workflow that wants
/// gateway-raised events exposes a signal with that same shape.
/// </summary>
public sealed class TemporalWorkflowGateway(
    ITemporalClient client, string taskQueue, IGatewayAuthorizer? authorizer = null) : IWorkflowGateway
{
    public async Task StartAsync(string instanceId, byte[] sealedSeed)
    {
        if (authorizer is not null)
        {
            await authorizer.AuthorizeStartAsync(instanceId);
        }

        byte[] seed = sealedSeed;   // a local for the expression-tree lambda
        await client.StartWorkflowAsync(
            (WorkflowOrchestration wf) => wf.Run(seed, 0),
            new WorkflowOptions(id: instanceId, taskQueue: taskQueue));
    }

    public async Task RaiseEventAsync(string instanceId, string eventName, byte[]? sealedPayload = null, string? raiseId = null)
    {
        if (authorizer is not null)
        {
            await authorizer.AuthorizeRaiseEventAsync(instanceId, eventName);
        }

        byte[] payload = sealedPayload ?? [];
        await client.GetWorkflowHandle<WorkflowOrchestration>(instanceId)
            .SignalAsync(wf => wf.RaiseEvent(eventName, payload, raiseId));
    }
}
