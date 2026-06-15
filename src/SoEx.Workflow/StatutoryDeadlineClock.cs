namespace SoEx.Workflow;

/// <summary>Where an erasure window came from — surfaced in reporting.</summary>
public enum DeadlineProvenance
{
    /// <summary>The consumer's <see cref="IDeadlinePolicy"/> supplied the window.</summary>
    ConsumerPolicy,

    /// <summary>No consumer policy; the framework's conservative default window is in use.</summary>
    DefaultFallback,
}

/// <summary>
/// Consumer-supplied statutory-window policy. The applicable window is
/// jurisdiction/regime-dependent, so the framework never hard-codes it — it asks the
/// consumer. Return <c>null</c> to defer to the framework's conservative default.
/// </summary>
public interface IDeadlinePolicy
{
    TimeSpan? Window(ErasureRequest request);
}

/// <summary>
/// The evaluated legal-clock state for an erasure request: the computed
/// <see cref="Deadline"/>, the <see cref="Remaining"/> time, whether the deadline is
/// near enough to raise a breach-risk escalation, and the window's provenance.
/// </summary>
public sealed record DeadlineStatus(
    DateTimeOffset ReceivedAt,
    DateTimeOffset Deadline,
    TimeSpan Remaining,
    bool EscalateBreachRisk,
    DeadlineProvenance Provenance)
{
    /// <summary>True when the conservative default window is in use (reporting flag).</summary>
    public bool UsingDefaultPolicy => Provenance == DeadlineProvenance.DefaultFallback;
}

/// <summary>
/// The legal clock: from the erasure-request received-time to the statutory
/// deadline. Governs <i>compliance</i> — never conflated with the operational
/// retry/quarantine cadence, which lives inside this window.
///
/// The window comes from the consumer's <see cref="IDeadlinePolicy"/>; with none, a
/// conservative, self-announcing default applies — deliberately tighter than any
/// plausible regime (erring short only ever alerts early; erring long risks an
/// actual breach), and flagged as "default policy in use." It is explicitly not a legal
/// determination; the consumer remains responsible for the actual window.
/// </summary>
public sealed class StatutoryDeadlineClock
{
    /// <summary>
    /// Conservative fallback window — far tighter than e.g. GDPR's one month, so a missing
    /// policy alerts early rather than risking a breach. Self-announcing via
    /// <see cref="DeadlineStatus.UsingDefaultPolicy"/>.
    /// </summary>
    public static readonly TimeSpan ConservativeDefaultWindow = TimeSpan.FromDays(7);

    private readonly IDeadlinePolicy? _policy;
    private readonly TimeSpan _escalateWithin;
    private readonly TimeProvider _time;

    /// <param name="policy">Consumer window policy; null defers entirely to the conservative default.</param>
    /// <param name="escalateWithin">How close to the deadline raises the breach-risk flag.</param>
    /// <param name="time">Clock source (inject a fake in tests).</param>
    public StatutoryDeadlineClock(IDeadlinePolicy? policy = null, TimeSpan? escalateWithin = null, TimeProvider? time = null)
    {
        _policy = policy;
        _escalateWithin = escalateWithin ?? TimeSpan.FromDays(1);
        _time = time ?? TimeProvider.System;
    }

    public DeadlineStatus Evaluate(ErasureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        TimeSpan? consumerWindow = _policy?.Window(request);
        TimeSpan window = consumerWindow ?? ConservativeDefaultWindow;
        DeadlineProvenance provenance = consumerWindow is null
            ? DeadlineProvenance.DefaultFallback
            : DeadlineProvenance.ConsumerPolicy;

        DateTimeOffset deadline = request.ReceivedAt + window;
        TimeSpan remaining = deadline - _time.GetUtcNow();

        // Escalate as the legal deadline approaches (or has passed) — never on an arbitrary
        // operational timer. The flag means "this erasure risks breaching its statutory window."
        bool escalate = remaining <= _escalateWithin;

        return new DeadlineStatus(request.ReceivedAt, deadline, remaining, escalate, provenance);
    }
}
