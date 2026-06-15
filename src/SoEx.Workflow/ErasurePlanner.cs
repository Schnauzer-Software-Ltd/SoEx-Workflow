namespace SoEx.Workflow;

/// <summary>How an erasure request is satisfied for a given instance.</summary>
public enum ErasureAction
{
    /// <summary>Let the instance finish on its own — it self-erases (key dies at natural
    /// termination) within the statutory window. Preferred for multi-subject instances, where
    /// force-terminating to satisfy one subject would damage co-subjects' lawful processing.</summary>
    CompleteNaturally,

    /// <summary>Drive the instance to its app-defined clean termination before the deadline.</summary>
    ForceTerminate,
}

/// <summary>
/// The bounded-vs-force decision — the payoff of the per-instance-key model: a
/// duration-vs-deadline comparison instead of a per-subject crypto-shred. Evaluated per
/// targeted instance against the request's <see cref="DeadlineStatus"/>.
/// </summary>
public sealed class ErasurePlanner
{
    private readonly TimeProvider _time;

    public ErasurePlanner(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>
    /// A bounded instance may complete naturally iff it will self-erase before the deadline:
    /// <c>now + maxRemaining &lt; deadline</c>. An unbounded instance (<paramref name="maxRemaining"/>
    /// null) — or any bounded one that could exceed the window — is force-terminated.
    /// </summary>
    public ErasureAction Decide(DeadlineStatus deadline, TimeSpan? maxRemaining)
    {
        ArgumentNullException.ThrowIfNull(deadline);

        if (maxRemaining is not { } bound)
        {
            return ErasureAction.ForceTerminate;   // unbounded / long-running
        }

        DateTimeOffset selfEraseBy = _time.GetUtcNow() + bound;
        return selfEraseBy < deadline.Deadline
            ? ErasureAction.CompleteNaturally
            : ErasureAction.ForceTerminate;
    }
}
