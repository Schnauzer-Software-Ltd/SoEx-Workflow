namespace SoEx.Workflow;

/// <summary>
/// The single per-runtime interface. The generic orchestration driver drives it;
/// the entrypoint never touches it. Each runtime adapter implements it (the in-memory
/// null-durability runtime is the Tier-1 baseline). Grows across Stage 3 with the
/// step/wait/event primitives.
/// </summary>
public interface IWorkflowRuntime
{
    /// <summary>Stable identity of this workflow instance. Journaled in clear, so it must be PII-free.</summary>
    string InstanceId { get; }

    /// <summary>The next per-instance step sequence number (0-based, monotonic, deterministic across replay).</summary>
    long NextSequence();

    /// <summary>Parks until a durable timer of <paramref name="delay"/> fires.</summary>
    Task DelayAsync(TimeSpan delay);

    /// <summary>Parks until the named (driver-owned) event is raised; returns its payload.</summary>
    Task<byte[]> WaitForEventAsync(string eventName);

    /// <summary>
    /// Raises a named event at an instance, resuming a parked wait (or buffering for a later wait). A
    /// non-null <paramref name="raiseId"/> makes the raise idempotent: a re-raise carrying the same id is
    /// a no-op. Keyed on the id, not the name — distinct raises of one name (or any with no id) all count.
    /// </summary>
    Task RaiseEventAsync(string instanceId, string eventName, byte[] payload, string? raiseId = null);

    T? GetState<T>(string key);

    void SetState<T>(string key, T value);

    bool ShouldContinueAsNew { get; }

    void ContinueAsNew(byte[] carryState);
}
