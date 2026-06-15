namespace SoEx.Workflow;

/// <summary>
/// The engine-agnostic client seam for driving workflows from <i>outside</i> the
/// orchestration — the counterpart of <see cref="IWorkflowRuntime"/>, which only the
/// driver touches from inside. A component reacting to an external trigger (a webhook,
/// a business event) holds one of these plus a <see cref="WorkflowSealer"/> and can
/// start or continue a flow knowing nothing but the instance id (typically re-derived
/// from business identity via <see cref="DeterministicInstanceId"/>). One implementation
/// per runtime adapter; completion observation is adapter-specific and off this seam.
/// </summary>
public interface IWorkflowGateway
{
    /// <summary>
    /// Submits a new instance with its sealed seed step. Completes when the runtime has
    /// accepted the instance, not when the workflow finishes.
    /// </summary>
    Task StartAsync(string instanceId, byte[] sealedSeed);

    /// <summary>
    /// Raises a named event at an instance. With a <paramref name="sealedPayload"/>, the
    /// payload becomes the next step (today's data-carrying semantics). With none, the
    /// driver resumes into the wait's journaled <c>OnEvent</c> continuation — so the caller
    /// needs no flow knowledge and no key material.
    /// <para>
    /// <paramref name="raiseId"/> opts the raise into idempotency: a re-raise carrying the <b>same</b>
    /// id at the same instance is a no-op (the driver records handled ids in its per-instance state), so
    /// an accidentally re-delivered raise drives the continuation once. It is keyed on the id, not the
    /// event name — two <i>distinct</i> raises of one event name (each its own business event) both
    /// count, as does any raise with no id (the default). Supply a stable id where the caller might
    /// retry; omit it for repeatable same-name events.
    /// </para>
    /// </summary>
    Task RaiseEventAsync(string instanceId, string eventName, byte[]? sealedPayload = null, string? raiseId = null);
}

/// <summary>
/// An optional authorization chokepoint every <see cref="IWorkflowGateway"/> consults before it
/// starts a flow or raises an event. The framework cannot know a consumer's auth policy, but it
/// owns the one place that policy is enforced — on every adapter — so authorization lives in a
/// single seam instead of "do it somewhere upstream yourself". Throw from either method to reject
/// the operation (the broker/runtime call never happens); return to allow it. A gateway built
/// without an authorizer allows everything, preserving the default behaviour.
/// </summary>
public interface IGatewayAuthorizer
{
    /// <summary>Throws to reject starting a flow at <paramref name="instanceId"/>; returns to allow it.</summary>
    Task AuthorizeStartAsync(string instanceId);

    /// <summary>Throws to reject raising <paramref name="eventName"/> at <paramref name="instanceId"/>; returns to allow it.</summary>
    Task AuthorizeRaiseEventAsync(string instanceId, string eventName);
}
