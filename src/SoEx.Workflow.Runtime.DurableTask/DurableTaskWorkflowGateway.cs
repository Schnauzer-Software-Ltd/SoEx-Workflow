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
    IGatewayAuthorizer? authorizer = null,
    GatewaySealGuard? guard = null) : IWorkflowGateway
{
    public async Task StartAsync(string instanceId, byte[] sealedSeed)
    {
        if (authorizer is not null)
        {
            await authorizer.AuthorizeStartAsync(instanceId);
        }

        guard?.RequireSealed(sealedSeed, "the start seed");

        // DTS/DTFx does not throw on a duplicate instance id — it dedupes/overwrites by id — so detect a live
        // run explicitly and reject it. A completed/failed/terminated instance is left to re-schedule (a fresh
        // generation under the same id), mirroring the in-memory adapter's "a finished id can be re-onboarded".
        OrchestrationMetadata? existing = await client.GetInstanceAsync(instanceId);
        if (existing is not null && existing.RuntimeStatus is
                OrchestrationRuntimeStatus.Running or OrchestrationRuntimeStatus.Pending or OrchestrationRuntimeStatus.Suspended)
        {
            throw new WorkflowInstanceAlreadyExistsException(instanceId);
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

        guard?.RequireSealed(sealedPayload, "the raise payload");
        guard?.GuardRaiseId(instanceId, raiseId);

        // The portable orchestration waits on a RaisedEvent wrapper carrying the optional raise id and keeps a
        // per-instance handled-id set, so a re-raise with the same id is dropped instead of delivering twice.
        await client.RaiseEventAsync(instanceId, eventName, new RaisedEvent(raiseId, sealedPayload ?? []));
    }
}
