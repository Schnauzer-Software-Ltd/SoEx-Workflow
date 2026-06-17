namespace SoEx.Workflow;

/// <summary>
/// A right-to-erasure request that has been admitted but not yet processed — the durable intake that lets the
/// utility acknowledge "forget subject S" immediately and shred on a later drain pass, rather than blocking the
/// caller for the whole fan-out. A lost or delayed request only delays the start of erasure (a completeness
/// gap the sweep and deadline review already backstop); it never decouples the shred itself, which stays a
/// single synchronous call inside the drain.
/// <para>
/// The record carries the <see cref="InstanceIds"/> resolved from the subject index at admit, not the subject:
/// instance ids are PII-free by construction, so a durable pending store holds no recoverable subject at rest
/// (the same invariant the durable erasure-request registry keeps). <see cref="RequestId"/> is likewise a
/// PII-free, stable handle. The cost of resolving at admit is that an instance started for the subject after
/// admit but before the drain is not in this set; the abandoned-instance sweep and a re-issued request are the
/// backstops for that window.
/// </para>
/// </summary>
public readonly record struct PendingErasureRequest(
    string RequestId, DateTimeOffset ReceivedAt, IReadOnlyList<string> InstanceIds);

/// <summary>
/// A monitoring snapshot of the pending intake: how many requests are admitted-but-not-yet-drained, and when
/// the oldest was received (<c>null</c> when the queue is empty). The age of the oldest undrained request is
/// the pre-drain analogue of the open-request deadline review — alert on it before it nears the statutory
/// deadline, so a stalled or unscheduled drain is caught rather than silently breaching.
/// </summary>
public readonly record struct PendingBacklog(int Count, DateTimeOffset? OldestReceivedAt);

/// <summary>
/// Records admitted-but-not-yet-drained erasure requests so the front door can accept them durably and a drain
/// pass can process them on the host's cadence. <see cref="ReceivedAt"/> is captured at admit so the statutory
/// clock anchors to when the request arrived, not when it drained. In-process by default
/// (<c>InMemoryPendingErasureRequests</c>); a durable, shared implementation lets the admit survive a crash
/// before the drain runs (the "durably accept before ack" guarantee) and lets a separate worker drain the fleet.
/// </summary>
public interface IPendingErasureRequests
{
    /// <summary>Admits (or replaces) a request to be drained. Idempotent on the request id.</summary>
    void Admit(PendingErasureRequest request);

    /// <summary>A snapshot of every admitted request not yet drained.</summary>
    IReadOnlyCollection<PendingErasureRequest> Pending();

    /// <summary>Marks a request drained (its fan-out has run). Idempotent.</summary>
    void Drained(string requestId);

    /// <summary>A snapshot of the backlog (count + oldest admit time) for health checks and the drain scheduler.
    /// A monitoring read, not the hot path: read it to alert before the oldest undrained request nears its
    /// statutory deadline.</summary>
    PendingBacklog Backlog();
}
