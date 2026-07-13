namespace SoEx.Workflow;

/// <summary>One named maintenance cadence: a pass to run immediately and then every <see cref="Interval"/>.</summary>
public sealed record MaintenancePass(string Name, bool Enabled, TimeSpan Interval, Func<CancellationToken, Task> Run);

/// <summary>
/// A dependency-free driver for the erasure maintenance backstops — sweep abandoned instances, re-drive held
/// ones, and review approaching deadlines — each on its own cadence. Like <see cref="ErasureSweepLoop"/> it
/// owns only the timing (<see cref="System.Threading.PeriodicTimer"/>, no hosting dependency): run it from a
/// <c>BackgroundService</c>, a hosted task, or a cron tick. A pass that throws does not stop its loop — the
/// backstop must outlive a transient failure, so the next tick retries. Disabled or zero-interval passes are
/// skipped, so a consumer can enable only the cadences they want.
/// <para>
/// This default runner does <b>no leader election</b>: run it on a single instance, or — for high
/// availability — host a dedicated scheduler separately that calls the utility's one-pass operations, so
/// exactly one node drives each pass.
/// </para>
/// </summary>
public sealed class WorkflowMaintenanceLoop(TimeProvider? time = null)
{
    /// <summary>
    /// Runs every enabled pass on its own cadence until <paramref name="cancellation"/> fires. Each pass is
    /// observed: <paramref name="onPass"/> fires (pass name + running <see cref="LoopPassHealth"/>) after a
    /// clean run and <paramref name="onError"/> after a throw — so a pass that has silently stopped making
    /// progress is visible instead of failing forever in an empty catch. A pass that throws does not stop its
    /// loop (the next tick retries); a throw from either hook is contained so it cannot kill the loop either.
    /// </summary>
    public Task RunAsync(
        IReadOnlyList<MaintenancePass> passes,
        Func<string, LoopPassHealth, Task>? onPass = null,
        Func<string, Exception, LoopPassHealth, Task>? onError = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(passes);
        return Task.WhenAll(passes
            .Where(p => p.Enabled && p.Interval > TimeSpan.Zero)
            .Select(p => RunPassAsync(p, onPass, onError, cancellation)));
    }

    private async Task RunPassAsync(
        MaintenancePass pass,
        Func<string, LoopPassHealth, Task>? onPass,
        Func<string, Exception, LoopPassHealth, Task>? onError,
        CancellationToken cancellation)
    {
        TimeProvider clock = time ?? TimeProvider.System;
        int consecutiveFailures = 0;
        DateTimeOffset? lastSuccess = null;
        using var timer = new PeriodicTimer(pass.Interval, clock);
        do
        {
            try
            {
                await pass.Run(cancellation);
                consecutiveFailures = 0;
                lastSuccess = clock.GetUtcNow();
                await Safe(() => onPass?.Invoke(pass.Name, new LoopPassHealth(consecutiveFailures, lastSuccess)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A single failed pass must not kill the backstop; the next interval retries it — but it is no
                // longer silent. Report it so an operator sees a pass that has stopped making progress.
                consecutiveFailures++;
                LoopPassHealth health = new(consecutiveFailures, lastSuccess);
                await Safe(() => onError?.Invoke(pass.Name, ex, health));
            }
        }
        while (await timer.WaitForNextTickAsync(cancellation).ConfigureAwait(false));
    }

    // An observability hook must never take a backstop down with it.
    private static async Task Safe(Func<Task?>? hook)
    {
        try
        {
            Task? t = hook?.Invoke();
            if (t is not null)
            {
                await t;
            }
        }
        catch
        {
            // swallowed on purpose: a logging/metrics/alerting failure is not a reason to stop the backstop.
        }
    }
}
