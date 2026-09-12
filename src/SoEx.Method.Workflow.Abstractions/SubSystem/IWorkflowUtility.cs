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
    /// <para>Returns <see cref="StartOutcome.AlreadyExists"/> when a run already owns the id (the deterministic
    /// id makes a re-derived duplicate start the common case), so the caller can surface a meaningful response
    /// rather than a raw backend error. The result is data, not an exception, because it must cross the proxy
    /// boundary the manager reaches the utility over.</para>
    /// </summary>
    Task<StartOutcome> StartAsync(string flowKey, string instanceId, string subject, object firstStep);

    /// <summary>
    /// Raises a business event onto <paramref name="instanceId"/>. The waiting flow resumes into its own
    /// pre-sealed continuation either way — <paramref name="eventData"/> does not replace it, it travels with
    /// it and reaches the step operation as a second argument. The data is sealed under the instance key, so it
    /// is only ever journaled as ciphertext and the termination shred reaches it.
    /// <para>Supply data when the raiser knows something the flow could not; leave it null when the event
    /// itself is the whole message. The flow's step operation must declare a parameter to receive it, or the
    /// raise is refused at the seal.</para>
    /// <para>One operation with an optional argument rather than two overloads: SoEx validates an endpoint
    /// contract for duplicate operation names and refuses the whole topology at composition, so a same-named
    /// overload on this seam would take every host down at start-up.</para>
    /// </summary>
    Task RaiseEventAsync(string flowKey, string instanceId, string eventName, object? eventData = null);

    /// <summary>Recovers the subjects the durable index still maps to an instance — backs a manager's
    /// <c>OnRetaining</c> must-retain carve-out while the per-instance key is still live.</summary>
    Task<string[]> SubjectsForAsync(string instanceId);

    /// <summary>
    /// The instances of <paramref name="flowKey"/> the durable index currently maps <paramref name="subject"/>
    /// to. The companion to <see cref="SubjectsForAsync"/>, but a different kind of call: an instance id is
    /// normally re-derived from business identity with <c>DeterministicInstanceId</c>, needing no lookup and no
    /// shared store, so reach for this only where derivation cannot answer. The index is additive, so an
    /// instance can gain subjects it was not started under, and no derivation from such a subject yields that
    /// instance's id — that is the case this call exists for.
    /// <para>Scoped to one flow on purpose. The index spans every flow and every entry component sharing this
    /// utility, so an unscoped answer would name instances belonging to a peer manager. The scope is the
    /// <c>{flowKey}-{32 hex}</c> shape <c>DeterministicInstanceId</c> mints: an instance started under an id
    /// minted by some other convention does not carry the flow in a readable form and so is never returned.
    /// The flow must be wired on this host, as it must be to start or raise.</para>
    /// <para>This does not decay at the crypto-shred the way <see cref="SubjectsForAsync"/> does: it matches
    /// the subject's one-way lookup token rather than opening a blob sealed under the instance key, so it
    /// answers right up until the edges are pruned at termination.</para>
    /// </summary>
    Task<string[]> InstancesForAsync(string flowKey, string subject);
}
