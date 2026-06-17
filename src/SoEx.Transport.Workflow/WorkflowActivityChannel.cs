using SoEx.Endpoint;
using SoEx.Topology;

namespace SoEx.Transport.Workflow;

/// <summary>
/// The outbound seam (<see cref="IChannel"/>): the durable boundary. For the in-process
/// null runtime it routes straight to the bound endpoint (the literal SoEx transport,
/// since there is no external runtime); the durable runtimes drive the endpoint from their
/// own activity instead.
/// </summary>
public sealed class WorkflowActivityChannel<I>(WorkflowListeners listeners) : IChannel
    where I : class
{
    private Binding? _binding;

    public IPipeline? Pipeline => _binding?.Pipeline;

    public void Bind(Binding binding) => _binding = binding;

    public Task<byte[]> InvokeResult(byte[] invocationRequest) =>
        listeners.ForAddress(_binding?.Transport.Address
            ?? throw new InvalidOperationException("the channel must be bound before use")).DispatchAsync(invocationRequest);
}
