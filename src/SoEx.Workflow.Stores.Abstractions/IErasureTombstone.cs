namespace SoEx.Workflow;

/// <summary>
/// Records that an instance's key was destroyed by an <b>erasure</b> — a subject erasure request or the
/// abandoned-instance sweep — so a later attempt to re-mint that instance's key is refused. Without this, a
/// raise arriving after the shred re-mints a fresh key and resumes the flow, processing new subject data behind
/// a request already reported <c>Complete</c>. A tombstone makes the erasure final: the flow cannot be
/// resurrected. It is distinct from a natural completion, which does NOT tombstone — a completed logical id may
/// legitimately be re-onboarded as a fresh generation.
/// <para>
/// Durable in production (the tombstone must outlive the process and the shred to keep its guarantee); the
/// in-memory implementation is the reference and test default. Optional: an instance with no tombstone wired
/// keeps the prior behaviour, so this is opt-in hardening.
/// </para>
/// </summary>
public interface IErasureTombstone
{
    /// <summary>Marks <paramref name="instanceId"/> erased. Idempotent.</summary>
    void Record(string instanceId);

    /// <summary>True if <paramref name="instanceId"/> was erased and must not be re-minted.</summary>
    bool IsErased(string instanceId);
}
