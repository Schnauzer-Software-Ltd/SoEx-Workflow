namespace SoEx.Workflow;

/// <summary>Why a termination transition is happening — both paths fire <c>OnRetaining</c>.</summary>
public enum TerminationTrigger
{
    NaturalCompletion,
    ErasureRequest,
}

/// <summary>Passed to <see cref="IErasureEvents.OnRetaining"/> while the key is still live.</summary>
public sealed record RetainingContext(string InstanceId, IdempotencyKey IdempotencyKey, TerminationTrigger Trigger);

/// <summary>Passed to <see cref="IErasureEvents.OnTerminated"/> after the key is destroyed. PII-free.</summary>
public sealed record TerminatedContext(string InstanceId);

/// <summary>Passed to <see cref="IErasureEvents.OnRetentionHeld"/> when extraction has failed past retry.</summary>
public sealed record RetentionHeldContext(string InstanceId, int Attempts, Exception LastError);

/// <summary>
/// The three mandatory consumer contracts a subsystem entrypoint hosted on a workflow binding
/// must implement — the one deliberate opt-in. A deliberate no-op is a visible,
/// explicit choice, never an accidental omission. The shared <i>Retaining</i> root
/// on the first and third is two faces of one retention obligation; <i>Terminated</i>
/// means the lifecycle actually finished.
/// </summary>
public interface IErasureEvents
{
    /// <summary>
    /// Pre-shred extract. Fires on every termination path (natural completion and
    /// erasure alike) while the payload is still readable. Writes must-retain data
    /// outward to a governed store; must be idempotent on the idempotency key.
    /// </summary>
    Task OnRetaining(RetainingContext context);

    /// <summary>Post-termination, post-shred PII-free bookkeeping: prune the index, audit, release locks.</summary>
    Task OnTerminated(TerminatedContext context);

    /// <summary>
    /// Extraction-failure quarantine (non-final): the key is retained, auto-retry
    /// stopped, and the instance flagged for an audited re-drive.
    /// </summary>
    Task OnRetentionHeld(RetentionHeldContext context);
}
