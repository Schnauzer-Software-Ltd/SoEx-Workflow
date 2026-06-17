using System.Text.Json;
using Grpc.Core;
using SoEx.Workflow;
using Zeebe.Client;

namespace SoEx.Workflow.Runtime.Zeebe;

/// <summary>
/// The <see cref="IWorkflowGateway"/> over a Zeebe gateway. <c>Start</c> creates a process instance of the
/// bound BPMN process, seeding the reserved variables (the PII-free logical instance id + the sealed seed);
/// <c>Raise</c> publishes a Zeebe message correlated on that instance id, which a BPMN message-catch event
/// in the flow is waiting for. The broker assigns its own numeric instance key, so the logical id rides as a
/// variable and the message correlation key — never as the broker key.
/// </summary>
public sealed class ZeebeWorkflowGateway(
    IZeebeClient client, string bpmnProcessId, IGatewayAuthorizer? authorizer = null) : IWorkflowGateway
{
    public async Task StartAsync(string instanceId, byte[] sealedSeed)
    {
        if (authorizer is not null)
        {
            await authorizer.AuthorizeStartAsync(instanceId);
        }

        // No duplicate-start guard on THIS path: the broker assigns every CreateProcessInstance its own unique
        // processInstanceKey and does not dedupe on a business variable, and this gateway holds no state to
        // dedupe across calls. Two starts of one logical id would therefore run two instances sharing the one
        // instance key, and the first to terminate would shred it from under the second. For a broker-enforced
        // single-active start use <see cref="StartByMessageAsync"/> (message-id dedup within a TTL) against a
        // process with a message start event; on this plain path start-idempotency stays the caller's/ingress
        // responsibility — start from a DeterministicInstanceId and gate re-entry at the seam.
        await client.NewCreateProcessInstanceCommand()
            .BpmnProcessId(bpmnProcessId)
            .LatestVersion()
            .Variables(StartVariables(instanceId, sealedSeed))
            .Send();
    }

    /// <summary>
    /// The ONLY process variables the framework writes at start: the PII-free logical instance id and the
    /// sealed (ciphertext) seed. Kept in one place so the adapter's journaled footprint is fixed and
    /// lock-in-testable — a consumer's own variables live alongside these but the framework adds nothing else.
    /// </summary>
    internal static string StartVariables(string instanceId, byte[] sealedSeed) =>
        JsonSerializer.Serialize(new ZeebeWorkflowHost.FrameworkVars
        {
            InstanceId = instanceId,
            Seed = Convert.ToBase64String(sealedSeed),
        });

    /// <summary>
    /// Starts via a BPMN <b>message start event</b> instead of <c>CreateProcessInstance</c>, giving the broker
    /// the single-active guarantee the plain <see cref="StartAsync"/> cannot: the start message is published
    /// with <c>messageId = instanceId</c>, and Zeebe deduplicates published messages by message id within the
    /// TTL window, so two starts of one logical id within that window create exactly one instance — the
    /// duplicate is dropped broker-side, no gateway state. The process must own a message start event for
    /// <paramref name="startMessageName"/>. Outside the TTL window a later start is a fresh instance (the
    /// dedup is time-bounded, like the raise-id dedup); size the TTL to your re-drive window.
    /// <para>Returns <c>true</c> when this call started a new instance, <c>false</c> when the broker deduped it
    /// as a duplicate of a start already in the window — an idempotent-start result the caller can act on.</para>
    /// </summary>
    public async Task<bool> StartByMessageAsync(string instanceId, byte[] sealedSeed, string startMessageName,
        TimeSpan? dedupWindow = null)
    {
        if (authorizer is not null)
        {
            await authorizer.AuthorizeStartAsync(instanceId);
        }

        // messageId = instanceId is the dedup token: the broker rejects a re-publish of the same id within the
        // TTL, so a duplicate start cannot spawn a second instance. The correlation key is the same logical id
        // (a start message carries one but does not correlate to a running instance).
        try
        {
            await client.NewPublishMessageCommand()
                .MessageName(startMessageName)
                .CorrelationKey(instanceId)
                .MessageId(instanceId)
                .TimeToLive(dedupWindow ?? TimeSpan.FromMinutes(5))
                .Variables(StartVariables(instanceId, sealedSeed))
                .Send();
            return true;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.AlreadyExists)
        {
            // The broker rejects a re-publish of an id already in the TTL window before any instance is created
            // — the dedup firing. A duplicate start is therefore an idempotent no-op (the one live instance
            // stands), not an error to surface: the single-active guarantee CreateProcessInstance can't give.
            return false;
        }
    }

    public async Task RaiseEventAsync(string instanceId, string eventName, byte[]? sealedPayload = null, string? raiseId = null)
    {
        if (authorizer is not null)
        {
            await authorizer.AuthorizeRaiseEventAsync(instanceId, eventName);
        }

        // An empty raise resumes the waiting catch event with no data; a payload-carrying raise rides under a
        // non-reserved key so it never clobbers the framework's instanceId/seed variables in the journal.
        string variables = sealedPayload is { Length: > 0 } payload
            ? JsonSerializer.Serialize(new Dictionary<string, string> { ["__event"] = Convert.ToBase64String(payload) })
            : "{}";

        // A non-zero time-to-live buffers the message so a raise that arrives before the flow has opened its
        // catch-event subscription (the start/continue seam can't know the flow's exact position) still
        // correlates once the subscription opens — instead of being dropped (Zeebe's default TTL is zero).
        var command = client.NewPublishMessageCommand()
            .MessageName(eventName)
            .CorrelationKey(instanceId)
            .TimeToLive(TimeSpan.FromMinutes(5))
            .Variables(variables);

        // Idempotent raise: Zeebe dedupes published messages by message id within the TTL window, so a
        // re-raise carrying the same id is dropped broker-side — no gateway or flow state needed.
        if (raiseId is not null)
        {
            command = command.MessageId(raiseId);
        }

        await command.Send();
    }
}
