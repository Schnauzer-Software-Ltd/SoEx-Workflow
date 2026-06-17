namespace SoEx.Workflow;

/// <summary>
/// A quarantined instance: its termination extraction (<c>OnRetaining</c>) failed past the retry boundary, so
/// its key is <b>retained</b> (non-final) pending an audited re-drive. <see cref="HeldAt"/> anchors any
/// backoff/escalation; <see cref="IdempotencyKey"/> is what a re-drive replays under.
/// </summary>
public readonly record struct HeldInstance(
    string InstanceId, IdempotencyKey IdempotencyKey, int Attempts, DateTimeOffset HeldAt, string? LastError);

/// <summary>
/// Records the instances quarantined by <see cref="TerminationCoordinator"/> so the maintenance backstop can
/// find and re-drive them — held state is otherwise only a one-shot <c>OnRetentionHeld</c> callback with
/// nowhere to look it up later. An entry appears when an instance is held and is cleared when it finally
/// terminates (a successful re-drive). In-process by default (<c>InMemoryHeldInstanceRegistry</c>); a durable,
/// shared implementation lets a separately-hosted scheduler re-drive holds across the fleet.
/// </summary>
public interface IHeldInstanceRegistry
{
    /// <summary>Records (or updates) a held instance. Idempotent on the instance id.</summary>
    void Record(HeldInstance held);

    /// <summary>Clears a held instance once it has terminated. Idempotent.</summary>
    void Clear(string instanceId);

    /// <summary>A snapshot of every instance currently held.</summary>
    IReadOnlyCollection<HeldInstance> Held();
}
