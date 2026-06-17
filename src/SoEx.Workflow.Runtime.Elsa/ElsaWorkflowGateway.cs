using Elsa.Common.Models;
using Elsa.Workflows.Management;
using Elsa.Workflows.Management.Entities;
using Elsa.Workflows.Management.Filters;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Filters;
using Elsa.Workflows.Runtime.Messages;
using Elsa.Workflows.Runtime.Options;
using Microsoft.Extensions.DependencyInjection;
using ElsaRuntime = Elsa.Workflows.Runtime.IWorkflowRuntime;

namespace SoEx.Workflow.Runtime.Elsa;

/// <summary>
/// The <see cref="IWorkflowGateway"/> over an Elsa host's registered-definition durable
/// path — the externally-triggerable mode (the in-host loop in <see cref="ElsaWorkflowHost"/>
/// stays for self-driving fixtures). Start creates-and-runs the registered definition with the
/// logical instance id as the Elsa <b>correlation id</b> (the id governance is anchored on);
/// the run executes in-process until it parks on a bookmark. Raise finds the parked instance by
/// correlation, resolves the event payload against the bookmark (an empty raise resumes into
/// the journaled OnEvent step via <see cref="ElsaEventPayload"/>) and resumes that bookmark.
/// </summary>
public sealed class ElsaWorkflowGateway(
    IServiceProvider services, string workflowDefinitionId, IGatewayAuthorizer? authorizer = null,
    IIdempotencyStore? idempotency = null) : IWorkflowGateway
{
    public async Task StartAsync(string instanceId, byte[] sealedSeed)
    {
        if (authorizer is not null)
        {
            await authorizer.AuthorizeStartAsync(instanceId);
        }

        var client = await services.GetRequiredService<ElsaRuntime>().CreateClientAsync();
        await client.CreateAndRunInstanceAsync(new CreateAndRunWorkflowInstanceRequest
        {
            WorkflowDefinitionHandle = WorkflowDefinitionHandle.ByDefinitionId(workflowDefinitionId, VersionOptions.Latest),
            CorrelationId = instanceId,
            Input = new Dictionary<string, object> { ["seed"] = Convert.ToBase64String(sealedSeed) },
        });
    }

    public async Task RaiseEventAsync(string instanceId, string eventName, byte[]? sealedPayload = null, string? raiseId = null)
    {
        if (authorizer is not null)
        {
            await authorizer.AuthorizeRaiseEventAsync(instanceId, eventName);
        }

        // Finds the parked instance by correlation, resolves the payload against the bookmark, resumes it.
        async Task<byte[]> ResumeOnce()
        {
            WorkflowInstance instance = await services.GetRequiredService<IWorkflowInstanceStore>()
                    .FindAsync(new WorkflowInstanceFilter { CorrelationId = instanceId })
                ?? throw new InvalidOperationException($"no Elsa workflow instance is correlated to '{instanceId}'");

            Bookmark bookmark = instance.WorkflowState.Bookmarks.FirstOrDefault(b => b.Name == eventName)
                ?? throw new InvalidOperationException($"instance '{instanceId}' is not waiting on '{eventName}'");

            byte[] payload = ElsaEventPayload.Resolve(bookmark, sealedPayload ?? []);
            await services.GetRequiredService<IWorkflowResumer>().ResumeAsync(
                new BookmarkFilter { WorkflowInstanceId = instance.Id, Name = eventName },
                new ResumeBookmarkOptions { Input = new Dictionary<string, object> { ["payload"] = Convert.ToBase64String(payload) } },
                default);
            return [];
        }

        // Idempotent raise: the gateway drives consumer-authored definitions, so there is no in-flow place to
        // record handled ids (unlike the replay-based drivers). Instead, route the resume through the wired
        // IIdempotencyStore keyed by the raise id — a re-raise returns the recorded result without re-resuming,
        // so the continuation fires once. Durable across restarts iff the store is. With no store wired the id
        // is rejected (louder than silently ignoring it). The "__raise:" prefix keeps these keys disjoint from
        // step idempotency keys.
        if (raiseId is not null && idempotency is not null)
        {
            await idempotency.ApplyOnceAsync(new IdempotencyKey(instanceId, "__raise:" + raiseId, 0), ResumeOnce);
            return;
        }

        RaiseIdNotSupported.ThrowIfSet(raiseId, "Elsa");
        await ResumeOnce();
    }
}
