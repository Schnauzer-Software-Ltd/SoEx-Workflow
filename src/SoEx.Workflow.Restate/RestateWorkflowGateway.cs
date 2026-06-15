using System.Text;
using System.Text.Json;

namespace SoEx.Workflow.Restate;

/// <summary>
/// The <see cref="IWorkflowGateway"/> over the Restate ingress HTTP API, targeting one sidecar
/// service (e.g. the generic portable flow, or a consumer-authored native workflow service).
/// Start submits the workflow's <c>run</c> handler fire-and-forget (<c>/send</c>) with the
/// base64 sealed seed; Raise posts the service's <c>raise_event</c> handler, which resolves
/// the named durable promise — an empty payload resumes the wait's pre-sealed OnEvent step.
/// </summary>
public sealed class RestateWorkflowGateway(
    Uri ingress, string serviceName, HttpClient? http = null, IGatewayAuthorizer? authorizer = null) : IWorkflowGateway
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    public async Task StartAsync(string instanceId, byte[] sealedSeed)
    {
        if (authorizer is not null)
        {
            await authorizer.AuthorizeStartAsync(instanceId);
        }

        HttpResponseMessage resp = await _http.PostAsync(
            new Uri(ingress, $"{serviceName}/{instanceId}/run/send"),
            JsonBody(Convert.ToBase64String(sealedSeed)));
        resp.EnsureSuccessStatusCode();
    }

    public async Task RaiseEventAsync(string instanceId, string eventName, byte[]? sealedPayload = null, string? raiseId = null)
    {
        if (authorizer is not null)
        {
            await authorizer.AuthorizeRaiseEventAsync(instanceId, eventName);
        }

        // Target the base instance id even across continue-as-new: after a Loop the live wait runs in a later
        // generation (keyed instanceId~<seq>), and the sidecar forwards a raise that lands on a completed
        // generation along the chain to the running one, so the gateway never needs to know the generation.
        // Idempotent raises are inherent on Restate: the sidecar resolves a durable promise keyed by the event
        // NAME, which is write-once, so a re-raise is a no-op (the sidecar peeks before resolving) — at least as
        // strong as the raise-id dedup the other sidecars implement, and it subsumes it. The raiseId is forwarded
        // for symmetry but is advisory here; Restate consequently cannot deliver two distinct raises of one name
        // (its latch model — a documented divergence the id would not change).
        HttpResponseMessage resp = await _http.PostAsync(
            new Uri(ingress, $"{serviceName}/{instanceId}/raise_event"),
            JsonBody(new { name = eventName, payload = Convert.ToBase64String(sealedPayload ?? []), raiseId }));
        resp.EnsureSuccessStatusCode();
    }

    private static StringContent JsonBody(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
}
