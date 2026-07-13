namespace SoEx.Workflow;

/// <summary>
/// Thrown by an <see cref="IWorkflowGateway"/> when a start is submitted for an instance id a run already owns
/// on the runtime — the durable engine's "one active run per id" boundary surfaced as a typed, engine-agnostic
/// signal a caller can act on (e.g. return a meaningful "already started" response) instead of a raw backend
/// error. Because the id is a <see cref="DeterministicInstanceId"/>, a re-derived duplicate start is the common
/// trigger; widen the identity (a fresh attempt/epoch segment) to begin a genuinely new run.
/// <para>It derives from <see cref="InvalidOperationException"/> so existing callers that catch that base — and
/// the in-memory adapter's original throw — keep working; catch this type where the already-exists case needs
/// to be told apart from other invalid operations.</para>
/// <para>Not every runtime can raise this on the plain start path: an engine that mints its own instance key
/// and does not dedupe on the supplied id (the Zeebe create-instance path) cannot detect the duplicate, so
/// start-idempotency there stays the caller's responsibility at the trigger seam.</para>
/// </summary>
public sealed class WorkflowInstanceAlreadyExistsException(string instanceId)
    : InvalidOperationException($"workflow instance '{instanceId}' is already running")
{
    /// <summary>The instance id whose start was rejected because a run already owns it.</summary>
    public string InstanceId { get; } = instanceId;
}
