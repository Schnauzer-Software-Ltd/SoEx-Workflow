using SoEx.Topology;

namespace SoEx.Transport.Workflow;

/// <summary>The workflow <see cref="Binding"/> — selected in hosting exactly where InProc/NamedPipe are today.</summary>
public sealed record WorkflowBinding<I> : Topology.Binding where I : class
{
    // Address by the full type, not the simple name: a component can expose more than one workflow
    // contract (e.g. a native and a portable step interface that share a simple name in different
    // namespaces), and each needs a distinct durable seam address. The full name goes in an escaped path
    // segment (it can contain characters — '+', '`' — that are invalid in a URI authority).
    public WorkflowBinding(string subSystem) : base(
        typeof(I),
        new WorkflowTransport
        {
            Address = new Address.Single(
                new Uri($"soex.workflow://{subSystem}/{Uri.EscapeDataString(typeof(I).FullName ?? typeof(I).Name)}")),
        },
        subSystem)
    {
    }
}
