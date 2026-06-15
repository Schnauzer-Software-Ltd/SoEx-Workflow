using System.Collections.Concurrent;

namespace SoEx.Workflow;

/// <summary>
/// Dispatches one opaque step envelope to the hosted entrypoint and returns the opaque
/// response envelope (a serialized <c>InvocationResponse</c> whose payload is the
/// <see cref="WorkflowAction"/>). A bound <c>WorkflowEndpoint</c> is the implementation;
/// it runs the SoEx endpoint pipeline.
/// </summary>
public interface IWorkflowDispatch
{
    Task<byte[]> DispatchAsync(byte[] stepEnvelope);
}

/// <summary>
/// Process-level registry of bound workflow endpoints, keyed by transport address —
/// the workflow analogue of <c>InProcListeners</c>. A <c>WorkflowEndpoint</c> registers
/// itself here when the host starts (<c>Listen</c>); the invoker resolves it to dispatch.
/// </summary>
public sealed class WorkflowListeners
{
    private readonly ConcurrentDictionary<Uri, IWorkflowDispatch> _endpoints = new();

    public void Register(Uri address, IWorkflowDispatch endpoint) => _endpoints[address] = endpoint;

    public IWorkflowDispatch ForAddress(Uri address) =>
        _endpoints.TryGetValue(address, out IWorkflowDispatch? endpoint)
            ? endpoint
            : throw new ArgumentException($"No workflow endpoint registered for address {address}");
}
