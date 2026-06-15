namespace SoEx.Workflow;

/// <summary>
/// The in-process orchestration driver on the governed-step core. InProc is the one
/// SoEx-provided flow option (it has no native backend to author against), so it keeps the
/// <see cref="WorkflowAction"/> step-loop: dispatch each step through <see cref="GovernedStep{I}"/>
/// (pipeline + per-step governance + idempotency), route the returned action against the
/// runtime, and run the termination erasure lifecycle via <see cref="GovernedTermination"/> on completion.
/// </summary>
public sealed class WorkflowDriver<I>(IWorkflowRuntime runtime, GovernedStep<I> step, GovernedTermination termination)
    where I : class
{
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
                object? result = await step.DispatchGovernedAsync(current, runtime.InstanceId, sequence);
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
        catch
        {
            // The termination is "completion, cancellation, or erasure": a failed or cancelled portable instance
            // still owes the crypto-shred, so run it before the failure propagates. This is the driver analogue
            // of the native finally-block. Idempotent — if a Complete already shredded, the key is gone and the
            // coordinator no-ops; continue-as-new stays in the loop above and never reaches here.
            await termination.TerminateAsync(
                runtime.InstanceId,
                new IdempotencyKey(runtime.InstanceId, "terminal", lastSequence),
                TerminationTrigger.NaturalCompletion);
            throw;
        }
    }

    private async Task<byte[]> AwaitEventAsync(WorkflowAction.WaitForEvent wait, byte[]? ambient)
    {
        string eventName = step.GuardVisibleName(wait.EventName, ambient);

        if (wait.Timeout is not { } timeout)
        {
            return Raised(wait, eventName, await runtime.WaitForEventAsync(eventName), ambient);
        }

        Task<byte[]> eventTask = runtime.WaitForEventAsync(eventName);
        Task timer = runtime.DelayAsync(timeout);
        Task finished = await Task.WhenAny(eventTask, timer);

        if (finished == eventTask)
        {
            return Raised(wait, eventName, await eventTask, ambient);
        }

        return wait.OnTimeout is { } onTimeout
            ? step.SealStep(runtime.InstanceId, onTimeout, ambient)
            : throw new InvalidOperationException(
                $"durable timer elapsed waiting for '{eventName}' with no OnTimeout step");
    }

    // An event raised with a payload carries the next step; one raised empty resumes into the
    // wait's OnEvent continuation (the flow decided at wait time what the bare event means).
    private byte[] Raised(WorkflowAction.WaitForEvent wait, string eventName, byte[] payload, byte[]? ambient)
    {
        if (payload is { Length: > 0 })
        {
            return payload;
        }

        return wait.OnEvent is { } onEvent
            ? step.SealStep(runtime.InstanceId, onEvent, ambient)
            : throw new InvalidOperationException(
                $"'{eventName}' was raised with an empty payload and the wait has no OnEvent step");
    }
}
