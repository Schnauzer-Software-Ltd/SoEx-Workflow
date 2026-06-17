using SoEx.Endpoint;
using SoEx.Topology;
using SoEx.Workflow;

namespace SoEx.Transport.Workflow;

/// <summary>
/// The inbound orchestration seam (<see cref="IEndpoint"/>) — hosts the entrypoint behind
/// the runtime's generic driver. Dispatch goes through the SoEx endpoint pipeline
/// (<see cref="IEndpointPipeline.ServicePipeLine{I}"/> → <c>DefaultDispatcher</c>), so the
/// entrypoint is invoked as a normal SoEx component by operation name with its typed argument
/// and full <c>IContextFlowPolicy</c> flow — the framework, never the entrypoint, cracks the
/// envelope.
/// </summary>
public sealed class WorkflowEndpoint<I>(IEndpointPipeline endpointPipeline, WorkflowListeners listeners) : IEndpoint, IWorkflowDispatch
    where I : class
{
    private Binding? _binding;

    public void Bind(Binding binding, string componentName) => _binding = binding;

    public Task Listen()
    {
        ArgumentNullException.ThrowIfNull(_binding, "the endpoint must be bound before listening");
        listeners.Register(_binding.Transport.Address, this);
        return Task.CompletedTask;
    }

    public Task Close() => Task.CompletedTask;

    public Task<byte[]> DispatchAsync(byte[] stepEnvelope) =>
        endpointPipeline.ServicePipeLine<I>(stepEnvelope, _binding?.Pipeline, null);
}
