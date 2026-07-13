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
    /// not stop the loop — the backstop must outlive a transient failure, so the next tick retries —
    /// but the failure is <b>not swallowed</b>: <paramref name="onError"/> receives the exception and the
    /// running <see cref="LoopPassHealth"/> (consecutive failures, last success), so an operator can alert on
    /// a backstop that has silently stopped. A throw from either hook is itself contained so it cannot kill
    /// the loop.
    /// </summary>
    public async Task RunAsync(
        TimeSpan interval,
        Func<SweepReport, Task>? onPass = null,
        Func<Exception, LoopPassHealth, Task>? onError = null,
        int? maxInstancesPerPass = null,
        CancellationToken cancellation = default)
    {
        TimeProvider clock = time ?? TimeProvider.System;
        int consecutiveFailures = 0;
        DateTimeOffset? lastSuccess = null;
        using var timer = new PeriodicTimer(interval, clock);
        do
        {
            try
            {
                // Thread the loop's cancellation into the sweep so shutdown stops mid-pass, and bound the pass so
                // a fleet-scale backlog drains over several ticks instead of one unbounded pass.
                SweepReport report = await coordinator.SweepAsync(olderThan, resolve, maxInstancesPerPass, cancellation);
                consecutiveFailures = 0;
                lastSuccess = clock.GetUtcNow();
                if (onPass is not null)
                {
                    await onPass(report);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A single failed pass must not kill the backstop; the next interval retries it — but it is no
                // longer silent. Report it so an operator sees a backstop that has stopped making progress.
                consecutiveFailures++;
                await SafeNotify(onError, ex, new LoopPassHealth(consecutiveFailures, lastSuccess));
            }
        }
        while (await timer.WaitForNextTickAsync(cancellation).ConfigureAwait(false));
    }

    private static async Task SafeNotify(
        Func<Exception, LoopPassHealth, Task>? onError, Exception ex, LoopPassHealth health)
    {
        if (onError is null)
        {
            return;
        }

        try
        {
            await onError(ex, health);
        }
        catch
        {
            // An alerting hook must never take the backstop down with it.
        }
    }
}
