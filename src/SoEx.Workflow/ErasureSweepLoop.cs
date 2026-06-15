namespace SoEx.Workflow;

/// <summary>
/// A dependency-free driver for <see cref="ErasureCoordinator.SweepAsync"/>: sweeps once, then on a
/// fixed interval until cancelled. The framework owns the shred logic; this owns the cadence, so a
/// consumer wires the abandoned-instance backstop with one call instead of hand-rolling a loop.
/// Run it from wherever you host background work (a <c>BackgroundService.ExecuteAsync</c>, a hosted
/// task, a cron tick) — core stays free of any hosting dependency.
/// </summary>
public sealed class ErasureSweepLoop(
    ErasureCoordinator coordinator,
    TimeSpan olderThan,
    Func<string, ErasureTarget?> resolve,
    TimeProvider? time = null)
{
    /// <summary>
    /// Sweeps immediately, then once every <paramref name="interval"/> until
    /// <paramref name="cancellation"/> fires. Each pass' <see cref="SweepReport"/> is handed to
    /// <paramref name="onPass"/> (a logging/metrics hook), when supplied. A pass that throws does
    /// not stop the loop — the backstop must outlive a transient failure, so the next tick retries.
    /// </summary>
    public async Task RunAsync(
        TimeSpan interval, Func<SweepReport, Task>? onPass = null, CancellationToken cancellation = default)
    {
        using var timer = new PeriodicTimer(interval, time ?? TimeProvider.System);
        do
        {
            try
            {
                SweepReport report = await coordinator.SweepAsync(olderThan, resolve);
                if (onPass is not null)
                {
                    await onPass(report);
                }
            }
            catch
            {
                // A single failed pass must not kill the backstop; the next interval retries it.
            }
        }
        while (await timer.WaitForNextTickAsync(cancellation).ConfigureAwait(false));
    }
}
