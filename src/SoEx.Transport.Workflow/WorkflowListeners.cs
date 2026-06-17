using System.Collections.Concurrent;
using SoEx.Workflow;

namespace SoEx.Transport.Workflow;

/// <summary>
/// Process-level registry of bound workflow endpoints, keyed by transport address —
/// the workflow analogue of <c>InProcListeners</c>. A <see cref="WorkflowEndpoint{I}"/> registers
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
