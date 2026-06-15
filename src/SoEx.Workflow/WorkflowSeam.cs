using System.Diagnostics.CodeAnalysis;
using SoEx.Endpoint;
using SoEx.Topology;

namespace SoEx.Workflow;

/// <summary>
/// The SoEx <see cref="Transport"/> for the workflow binding: points the client/host
/// channels at the workflow seam. Shared across runtimes (they differ only by their
/// <see cref="IWorkflowRuntime"/>). A new runtime = this shape + a runtime — zero SoEx edits.
/// </summary>
public sealed class WorkflowTransport : Transport
{
    public WorkflowTransport()
    {
        ClientChannel = typeof(WorkflowActivityChannel<>);
        HostChannel = typeof(WorkflowEndpoint<>);
    }
}

/// <summary>The workflow <see cref="Binding"/> — selected in hosting exactly where InProc/NamedPipe are today.</summary>
public sealed class WorkflowBinding<I> : Binding where I : class
{
    [SetsRequiredMembers]
    public WorkflowBinding(string subSystem)
    {
        Contract = typeof(I);
        SubSystem = subSystem;
        Transport = new WorkflowTransport { Address = new Uri($"soex.workflow://{subSystem}-{typeof(I).Name}") };
    }
}

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
