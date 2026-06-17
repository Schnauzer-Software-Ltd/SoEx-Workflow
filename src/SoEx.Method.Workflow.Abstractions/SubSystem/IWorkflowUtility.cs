namespace SoEx.Method.Workflow.SubSystem;

/// <summary>
/// The <b>subsystem-internal</b> face of the Workflow utility — what a peer component (the manager) calls
/// through a SoEx proxy. It is a distinct contract type from the utility's <see cref="SoEx.Method.Workflow.External.IWorkflowUtility"/> face on
/// purpose: SoEx resolves a contract to exactly one channel per container, so the operations a component
/// proxies to must not share a type with the operations the host calls as a system client. These calls cross
/// a network transport when the components live in separate hosts, so they go through proxies — not shared
/// in-process references.
/// </summary>
public interface IWorkflowUtility
{
    /// <summary>
    /// Starts a flow: seals <paramref name="firstStep"/> under <paramref name="instanceId"/>'s key (binding the
    /// subject's ambient) and starts the flow on the runtime wired for <paramref name="flowKey"/>.
    /// <paramref name="firstStep"/> is opaque to the utility — it is sealed as the seed and only ever journaled
    /// as ciphertext. The caller derives the PII-free <paramref name="instanceId"/> (a stateless hash of identity).
    /// </summary>
    Task StartAsync(string flowKey, string instanceId, string subject, object firstStep);

    /// <summary>Raises a bare business event onto <paramref name="instanceId"/> — the waiting flow resumes into
    /// its own pre-sealed continuation.</summary>
    Task RaiseEventAsync(string flowKey, string instanceId, string eventName);

    /// <summary>Recovers the subjects the durable index still maps to an instance — backs a manager's
    /// <c>OnRetaining</c> must-retain carve-out while the per-instance key is still live.</summary>
    Task<string[]> SubjectsForAsync(string instanceId);
}
