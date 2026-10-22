namespace SoEx.Workflow;

/// <summary>Why an instance is in the held log. The two kinds recover differently.</summary>
public enum HeldKind
{
    /// <summary>The run ended, but its termination extraction (<c>OnRetaining</c>) failed past the retry
    /// boundary. The maintenance re-drive retries the extraction and then shreds.</summary>
    RetentionHold = 0,

    /// <summary>A step failed and the framework parked the run. No runtime can resume a parked run, so the
    /// maintenance re-drive leaves it alone. A deliberate termination (an erasure request, a force-terminate, or
    /// the abandoned-instance sweep) shreds it.</summary>
    Parked = 1,
}

/// <summary>
/// A quarantined instance: its key is <b>retained</b> (non-final). <see cref="Kind"/> says why — a failed
/// termination extraction pending an audited re-drive, or a parked step failure. <see cref="HeldAt"/> anchors
/// any backoff/escalation; <see cref="IdempotencyKey"/> is what a re-drive replays under.
/// </summary>
public readonly record struct HeldInstance(
    string InstanceId, IdempotencyKey IdempotencyKey, int Attempts, DateTimeOffset HeldAt, string? LastError,
    HeldKind Kind = HeldKind.RetentionHold);

/// <summary>
/// Records the instances quarantined by <see cref="TerminationCoordinator"/> so the maintenance backstop can
/// find and re-drive them — held state is otherwise only a one-shot <c>OnRetentionHeld</c> callback with
/// nowhere to look it up later. An entry appears when an instance is held and is cleared when it finally
/// terminates (a successful re-drive, or a deliberate termination of a park). In-process by default
/// (<c>InMemoryHeldInstanceRegistry</c>); a durable, shared implementation lets a separately-hosted scheduler
/// re-drive holds across the fleet.
/// </summary>
public interface IHeldInstanceRegistry
{
    /// <summary>Records (or updates) a held instance, including its <see cref="HeldInstance.Kind"/>. Idempotent
    /// on the instance id; a re-record replaces the entry.</summary>
    void Record(HeldInstance held);

    /// <summary>Clears a held instance once it has terminated. Idempotent.</summary>
    void Clear(string instanceId);

    /// <summary>A snapshot of every instance currently held.</summary>
    IReadOnlyCollection<HeldInstance> Held();
}
