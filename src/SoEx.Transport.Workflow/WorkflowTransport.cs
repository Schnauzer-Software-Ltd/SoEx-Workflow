using SoEx.Topology;
using SoEx.Workflow;

namespace SoEx.Transport.Workflow;

/// <summary>
/// The SoEx <see cref="Transport"/> for the workflow binding: points the client/host
/// channels at the workflow seam. Shared across runtimes (they differ only by their
/// <see cref="IWorkflowRuntime"/>). A new runtime = this shape + a runtime — zero SoEx edits.
/// </summary>
public sealed class WorkflowTransport : Topology.Transport
{
    public WorkflowTransport()
    {
        ClientChannel = typeof(WorkflowActivityChannel<>);
        HostChannel = typeof(WorkflowEndpoint<>);
    }
}
