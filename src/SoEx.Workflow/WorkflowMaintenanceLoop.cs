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
    /// <summary>Runs every enabled pass on its own cadence until <paramref name="cancellation"/> fires.</summary>
    public Task RunAsync(IReadOnlyList<MaintenancePass> passes, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(passes);
        return Task.WhenAll(passes
            .Where(p => p.Enabled && p.Interval > TimeSpan.Zero)
            .Select(p => RunPassAsync(p, cancellation)));
    }

    private async Task RunPassAsync(MaintenancePass pass, CancellationToken cancellation)
    {
        using var timer = new PeriodicTimer(pass.Interval, time ?? TimeProvider.System);
        do
        {
            try
            {
                await pass.Run(cancellation);
            }
            catch
            {
                // A single failed pass must not kill the backstop; the next interval retries it.
            }
        }
        while (await timer.WaitForNextTickAsync(cancellation).ConfigureAwait(false));
    }
}
