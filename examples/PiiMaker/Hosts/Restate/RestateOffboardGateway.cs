using System.Text;
using System.Text.Json;
using SoEx.Workflow;

namespace PiiMaker.Host.Restate;

/// <summary>
/// The gateway that submits the consumer-authored native offboarding fan-out (a Restate service call,
/// fire-and-forget via the ingress <c>/send</c> suffix). The fan-out completes on its own and shreds at the
/// termination, so there are no continuation events.
/// </summary>
sealed class RestateOffboardGateway(HttpClient http, string ingress) : IWorkflowGateway
{
    public async Task StartAsync(string instanceId, byte[] sealedSeed)
    {
        var body = new StringContent(JsonSerializer.Serialize(Convert.ToBase64String(sealedSeed)), Encoding.UTF8, "application/json");
        (await http.PostAsync($"{ingress}/MembershipOffboard/{instanceId}/run/send", body)).EnsureSuccessStatusCode();
    }

    public Task RaiseEventAsync(string instanceId, string eventName, byte[]? sealedPayload = null, string? raiseId = null) =>
        throw new NotSupportedException("the offboarding fan-out completes on its own; it has no continuation events");
}
