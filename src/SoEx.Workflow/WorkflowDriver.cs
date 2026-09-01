namespace SoEx.Workflow;

/// <summary>
/// The in-process orchestration driver on the governed-step core. InProc is the one
/// SoEx-provided flow option (it has no native backend to author against), so it keeps the
/// <see cref="WorkflowAction"/> step-loop: dispatch each step through <see cref="GovernedStep{I}"/>
/// (pipeline + per-step governance + idempotency), route the returned action against the
/// runtime, and run the termination erasure lifecycle via <see cref="GovernedTermination"/> on completion.
/// </summary>
public sealed class WorkflowDriver<I>(
    IWorkflowRuntime runtime, GovernedStep<I> step, GovernedTermination termination, WorkflowStepOptions? options = null)
    where I : class
{
    private readonly WorkflowStepOptions _options = options ?? WorkflowStepOptions.Default;

    public async Task<byte[]> RunAsync(byte[] seedStep)
    {
        byte[] current = seedStep;
        long lastSequence = 0;

        try
        {
            // The instance id is journaled in clear, so it must not carry the subject (it would survive the shred).
            step.GuardVisibleName(runtime.InstanceId, step.AmbientOf(runtime.InstanceId, seedStep));

            while (true)
            {
                long sequence = runtime.NextSequence();
                lastSequence = sequence;
                object? result = await DispatchWithRetryAsync(current, sequence);
                WorkflowAction action = result as WorkflowAction
                    ?? throw new InvalidOperationException(
                        $"the '{step.OperationName}' operation did not return a {nameof(WorkflowAction)}");
                IdempotencyKey key = step.KeyFor(current, runtime.InstanceId, sequence);
                byte[]? ambient = step.AmbientOf(runtime.InstanceId, current);

                switch (action)
                {
                    case WorkflowAction.Complete complete:
                        byte[] resultBytes = step.GuardResultPiiFree(step.Serializer.Serialize(complete.Result), ambient);
                        await termination.TerminateAsync(runtime.InstanceId, key, TerminationTrigger.NaturalCompletion);
                        return resultBytes;

                    case WorkflowAction.RaiseIntoNext raise:
                        current = step.SealStep(runtime.InstanceId, raise.NextStep, ambient);
                        break;

                    case WorkflowAction.WaitForEvent wait:
                        current = await AwaitEventAsync(wait, ambient);
                        break;

                    case WorkflowAction.Delay delay:
                        await runtime.DelayAsync(delay.Duration);
                        break;

                    case WorkflowAction.Loop loop:
                        byte[] carry = step.SealStep(runtime.InstanceId, loop.CarryState, ambient);
                        runtime.ContinueAsNew(carry);
                        current = carry;
                        break;

                    default:
                        throw new InvalidOperationException($"unhandled workflow action: {action.Kind()}");
                }
            }
        }
        catch (Exception error)
        {
            // Park-before-shred: a failed portable instance is quarantined with its key RETAINED — a transient
            // step failure must never crypto-shred the sealed journal (the old behaviour destroyed the instance
            // on the first blip). The key is destroyed only on a deliberate termination: a Complete above, or an
            // erasure/force-terminate driven through the coordinator. An operator re-drives the held instance or
            // terminates it explicitly. Idempotent — if a Complete already shredded, the key is gone and the
            // coordinator treats the park as a no-op; continue-as-new stays in the loop and never reaches here.
            await termination.QuarantineAsync(
                runtime.InstanceId,
                new IdempotencyKey(runtime.InstanceId, "terminal", lastSequence),
                _options.EffectiveMaxAttempts,
                error);
            throw;
        }
    }

    // Runs one governed step under the binding's failure policy: retry the dispatch on a non-terminal failure
    // with bounded exponential backoff, up to MaxAttempts. When the attempts are spent (or the failure is
    // classified terminal) the exception propagates to RunAsync's catch, which parks the instance. Retry
    // backoff is a real in-execution wait (Task.Delay), not the durable timer — a redelivered step re-enters
    // here fresh, and the step's idempotency (when wired) collapses a re-run that already recorded its effect.
    private async Task<object?> DispatchWithRetryAsync(byte[] current, long sequence)
    {
        int attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                return await step.DispatchGovernedAsync(current, runtime.InstanceId, sequence);
            }
            catch (Exception ex) when (_options.ShouldRetry(attempt, ex))
            {
                await Task.Delay(_options.DelayBefore(attempt));
            }
        }
    }

    // Event waits already registered against the runtime but not yet consumed, keyed by event name.
    //
    // A multi-branch wait races N waits against the timer and only one can win, so the losers are left
    // registered. Dropping them would drop their payloads: a raise that lands on an abandoned wait resolves
    // it into nothing, and the NEXT wait on that name — having neither a live waiter nor a buffered value —
    // parks forever. That is exactly the swallowed-event failure multi-branch waits exist to remove, so the
    // losing waits are stashed here and reused by the next wait that names them. It also closes the same hole
    // in a single-branch wait whose timer won (the event task was abandoned the same way).
    private readonly Dictionary<string, Task<byte[]>> _pendingWaits = [];

    private Task<byte[]> PendingWait(string eventName)
    {
        if (!_pendingWaits.TryGetValue(eventName, out Task<byte[]>? pending))
        {
            _pendingWaits[eventName] = pending = runtime.WaitForEventAsync(eventName);
        }

        return pending;
    }

    private async Task<byte[]> AwaitEventAsync(WorkflowAction.WaitForEvent wait, byte[]? ambient)
    {
        // EVERY branch name is journaled in clear, so every one is guarded — not just the first.
        string[] names = [.. wait.Branches.Select(b => step.GuardVisibleName(b.EventName, ambient))];
        Task<byte[]>[] waits = [.. names.Select(PendingWait)];

        List<Task> racing = [.. waits];
        if (wait.Timeout is { } timeout)
        {
            racing.Add(runtime.DelayAsync(timeout));
        }

        await Task.WhenAny(racing);

        // Re-scan in DECLARED order instead of taking whichever task WhenAny surfaced: when more than one of
        // the wait's events is already deliverable, the first branch declared wins. That makes the choice a
        // property of the flow rather than of the runtime's delivery order.
        for (int i = 0; i < waits.Length; i++)
        {
            if (waits[i].IsCompleted)
            {
                _pendingWaits.Remove(names[i]);
                return Raised(wait.Branches[i], names[i], await waits[i], ambient);
            }
        }

        return wait.OnTimeout is { } onTimeout
            ? step.SealStep(runtime.InstanceId, onTimeout, ambient)
            : throw new InvalidOperationException(
                $"durable timer elapsed waiting for {string.Join(", ", names.Select(n => $"'{n}'"))} with no OnTimeout step");
    }

    // An event raised with a payload carries the next step; one raised empty resumes into the continuation
    // its OWN branch declared (the flow decided at wait time what that bare event means).
    private byte[] Raised(EventBranch branch, string eventName, byte[] payload, byte[]? ambient)
    {
        if (payload is { Length: > 0 })
        {
            return payload;
        }

        return branch.OnEvent is { } onEvent
            ? step.SealStep(runtime.InstanceId, onEvent, ambient)
            : throw new InvalidOperationException(
                $"'{eventName}' was raised with an empty payload and its branch of the wait has no OnEvent step");
    }
}
