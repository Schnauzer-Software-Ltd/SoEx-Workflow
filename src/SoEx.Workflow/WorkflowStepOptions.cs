namespace SoEx.Workflow;

/// <summary>
/// The per-step failure policy a binding sets: how a failing governed step is retried, how long it may run,
/// and which exceptions are terminal (never retried). One documented cross-runtime default — bounded
/// exponential backoff, then, when retries are exhausted, <b>park-before-shred</b>: the instance is
/// quarantined with its key <b>retained</b> (recorded held, <c>OnRetentionHeld</c> fired), never
/// crypto-shred, so a transient blip can no longer destroy the sealed journal. An operator re-drives a held
/// instance (resume) or deliberately terminates it (which then shreds). Each adapter maps this onto its
/// engine's native retry primitive (Temporal <c>RetryPolicy</c>, DTFx <c>TaskOptions</c>, Zeebe job retries);
/// the in-process and Elsa drivers apply it directly around the step dispatch.
/// </summary>
public sealed record WorkflowStepOptions
{
    /// <summary>Maximum total attempts for a failing step (clamped to &gt;= 1). 1 = no retry (park on first failure).</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Delay before the first retry; later retries scale by <see cref="BackoffCoefficient"/> up to <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan FirstRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Exponential backoff multiplier applied to the retry delay each attempt.</summary>
    public double BackoffCoefficient { get; init; } = 2.0;

    /// <summary>Ceiling on any single retry delay.</summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Per-step run budget the adapter enforces (Temporal/DTFx StartToClose, Zeebe job lock). Null = the adapter default.</summary>
    public TimeSpan? StepTimeout { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// A terminal exception is never retried — the step goes straight to park. Null = every failure retries up
    /// to <see cref="MaxAttempts"/>. Lets a binding classify a deterministic failure (bad input, a 4xx) as
    /// non-retryable while a transient one (a timeout, a 5xx) rides the backoff.
    /// </summary>
    public Func<Exception, bool>? IsTerminal { get; init; }

    /// <summary>The one documented cross-runtime default: bounded exponential backoff, then park.</summary>
    public static WorkflowStepOptions Default { get; } = new();

    /// <summary>No retry — park on the first failure (for genuinely non-retryable steps, and to keep tests fast).</summary>
    public static WorkflowStepOptions NoRetry { get; } = new() { MaxAttempts = 1 };

    /// <summary>The effective attempt cap, never below 1.</summary>
    public int EffectiveMaxAttempts => Math.Max(1, MaxAttempts);

    /// <summary>True if a failure on <paramref name="attempt"/> (1-based) should be retried rather than parked.</summary>
    public bool ShouldRetry(int attempt, Exception error) =>
        attempt < EffectiveMaxAttempts && !(IsTerminal?.Invoke(error) ?? false);

    /// <summary>The backoff delay before the retry that follows <paramref name="attempt"/> (1-based), capped at <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan DelayBefore(int attempt)
    {
        double ms = FirstRetryDelay.TotalMilliseconds * Math.Pow(BackoffCoefficient, Math.Max(0, attempt - 1));
        return TimeSpan.FromMilliseconds(Math.Min(ms, MaxRetryDelay.TotalMilliseconds));
    }
}
