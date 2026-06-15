using SoEx.Abstractions;
using SoEx.Workflow;

namespace SoEx.Method.Workflow;

/// <summary>
/// The workflow seam the host wires into the <see cref="SubSystem.IWorkflowUtility"/> after composing the system, keyed
/// by a string <c>flowKey</c> (the same string the manager passes to start/raise). Per flow: the
/// engine-agnostic <see cref="IWorkflowGateway"/> (start + raise on whatever runtime the host chose), the
/// <see cref="WorkflowSealer"/> for that flow's governed operation (seal-side only — a component cannot hold
/// the dispatch endpoint, which dispatches <i>into</i> it), and the serializer for ambient subject bytes.
/// Deferred and settable because the gateways can only be built after the system is composed; a start/raise on
/// an unwired flow fails with a clear message. This is the live, non-serializable wiring the utility holds —
/// it never crosses the proxy.
/// </summary>
public sealed class WorkflowSeam
{
    /// <summary>One flow's wiring: how to start/raise events on it and how to seal its steps.</summary>
    public sealed record FlowSeam(IWorkflowGateway Gateway, WorkflowSealer Sealer, IMessageSerializer Serializer);

    private readonly Dictionary<string, FlowSeam> _flows = new(StringComparer.Ordinal);

    /// <summary>Wires (or rewires) one flow. Called by the host at startup, before any start/raise is served.</summary>
    public void Connect(string flowKey, IWorkflowGateway gateway, WorkflowSealer sealer, IMessageSerializer serializer) =>
        _flows[flowKey] = new FlowSeam(gateway, sealer, serializer);

    /// <summary>The wiring for a flow, or a clear failure if this host did not wire it (e.g. a native-only flow
    /// on a portable runtime).</summary>
    public FlowSeam For(string flowKey) =>
        _flows.TryGetValue(flowKey, out FlowSeam? flow) ? flow
            : throw new InvalidOperationException($"the '{flowKey}' flow is not wired to a workflow runtime on this host");
}
