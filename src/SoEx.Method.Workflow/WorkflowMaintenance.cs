using SoEx.Workflow;

namespace SoEx.Method.Workflow;

/// <summary>
/// The built-in erasure-maintenance cadence — the dependency-free default. Each task is independently
/// toggleable; intervals tune the cadence. <see cref="Enabled"/> is the single switch the host reads to start
/// (or not start) the runner. <b>For production / high availability, leave this off and host a dedicated
/// scheduler separately</b> that calls the utility's one-pass operations, so exactly one node drives each pass
/// (this default does no leader election).
/// </summary>
public sealed record WorkflowMaintenanceOptions
{
    /// <summary>Master switch the host reads to decide whether to run the built-in runner at all.</summary>
    public bool Enabled { get; init; }

    public bool Drain { get; init; } = true;
    /// <summary>How often to drain admitted erasure requests (the request-driven shred). Erasure is request-and-drain,
    /// so this pass is what actually crypto-shreds a filed request — keep its interval well inside your deadline.</summary>
    public TimeSpan DrainInterval { get; init; } = TimeSpan.FromMinutes(1);

    public bool Sweep { get; init; } = true;
    /// <summary>How often to sweep abandoned instances.</summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromMinutes(15);
    /// <summary>Minimum age before an un-terminated instance is swept — must exceed the longest legitimate flow.</summary>
    public TimeSpan SweepOlderThan { get; init; } = TimeSpan.FromDays(1);

    public bool ReDriveHeld { get; init; } = true;
    /// <summary>How often to re-drive quarantined (held) instances.</summary>
    public TimeSpan HeldReDriveInterval { get; init; } = TimeSpan.FromMinutes(5);

    public bool ReviewDeadlines { get; init; } = true;
    /// <summary>How often to re-evaluate open erasure requests against the statutory clock.</summary>
    public TimeSpan DeadlineReviewInterval { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>How close to a statutory deadline forces an instance left to complete naturally.</summary>
    public TimeSpan EscalateWithin { get; init; } = TimeSpan.FromDays(1);
}

/// <summary>
/// Runs the built-in maintenance cadence by calling the utility's one-pass operations
/// (<c>DrainEraseRequestsAsync</c> / <c>SweepAbandonedAsync</c> / <c>ReDriveHeldAsync</c> /
/// <c>ReviewDeadlinesAsync</c>) on their intervals via the dependency-free <see cref="WorkflowMaintenanceLoop"/>.
/// The drain is what crypto-shreds a filed request, so it is on by default. The host fire-and-forgets this when
/// <see cref="WorkflowMaintenanceOptions.Enabled"/> — the configuration option within the workflow-utility
/// hosting. A separately-hosted production scheduler would call the same operations itself.
/// </summary>
public static class WorkflowMaintenance
{
    public static Task RunAsync(
        External.IWorkflowUtility utility,
        WorkflowMaintenanceOptions options,
        CancellationToken cancellation = default,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(utility);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return Task.CompletedTask; // disabled — production hosts a dedicated scheduler instead.
        }

        var passes = new List<MaintenancePass>
        {
            new("drain", options.Drain, options.DrainInterval,
                _ => utility.DrainEraseRequestsAsync()),
            new("sweep", options.Sweep, options.SweepInterval,
                _ => utility.SweepAbandonedAsync((long)options.SweepOlderThan.TotalSeconds)),
            new("held-redrive", options.ReDriveHeld, options.HeldReDriveInterval,
                _ => utility.ReDriveHeldAsync()),
            new("deadline-review", options.ReviewDeadlines, options.DeadlineReviewInterval,
                _ => utility.ReviewDeadlinesAsync((long)options.EscalateWithin.TotalSeconds)),
        };

        return new WorkflowMaintenanceLoop(time).RunAsync(passes, cancellation);
    }
}
