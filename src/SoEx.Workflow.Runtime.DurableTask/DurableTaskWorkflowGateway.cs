using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;

namespace SoEx.Workflow.Runtime.DurableTask;

/// <summary>
/// The <see cref="IWorkflowGateway"/> over a Durable Task Scheduler client. Start schedules
/// the portable flow's orchestration with the sealed seed by default; a native flow whose
/// orchestration input is derivable from the seed passes its orchestration name plus a
/// <paramref name="startInput"/> factory. Raise delivers an external event carrying a
/// <see cref="RaisedEvent"/> wrapper (the sealed step bytes plus an optional raise id) so the portable
/// driver can dedupe a re-raise; a native orchestration that wants gateway-raised events waits with
/// <c>WaitForExternalEvent&lt;RaisedEvent&gt;</c> (or raises its own payload type directly off the client).
/// </summary>
public sealed class DurableTaskWorkflowGateway(
    DurableTaskClient client,
    string orchestrationName = DurableTaskWorkflowHost.OrchestrationName,
    Func<byte[], object>? startInput = null,
    IGatewayAuthorizer? authorizer = null) : IWorkflowGateway
{
    public async Task StartAsync(string instanceId, byte[] sealedSeed)
    {
        if (authorizer is not null)
        {
            await authorizer.AuthorizeStartAsync(instanceId);
        }

        await client.ScheduleNewOrchestrationInstanceAsync(
            orchestrationName,
            startInput?.Invoke(sealedSeed) ?? new PortableSeed(sealedSeed),
            new StartOrchestrationOptions(instanceId));
    }

    public async Task RaiseEventAsync(string instanceId, string eventName, byte[]? sealedPayload = null, string? raiseId = null)
    {
        if (authorizer is not null)
        {
            await authorizer.AuthorizeRaiseEventAsync(instanceId, eventName);
        }

        // The portable orchestration waits on a RaisedEvent wrapper carrying the optional raise id and keeps a
        // per-instance handled-id set, so a re-raise with the same id is dropped instead of delivering twice.
        await client.RaiseEventAsync(instanceId, eventName, new RaisedEvent(raiseId, sealedPayload ?? []));
    }
}
