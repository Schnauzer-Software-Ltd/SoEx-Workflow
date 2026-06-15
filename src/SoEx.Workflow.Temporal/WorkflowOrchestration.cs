using Temporalio.Workflows;
using Wf = Temporalio.Workflows.Workflow;

namespace SoEx.Workflow.Temporal;

/// <summary>
/// Portable flow — the Temporal workflow, this runtime's generic driver (the replay path). It
/// drives the step loop by scheduling the entrypoint step as a Temporal activity and routing the returned
/// <see cref="WorkflowAction"/> (flattened to <see cref="WorkflowActionDto"/>) onto Temporal's
/// deterministic primitives (timers, signals via WaitConditionAsync, continue-as-new). The entrypoint runs
/// off the replay path in the activity; the workflow does no polymorphic deserialization. Use this or
/// the native flow (a consumer-authored <c>[Workflow]</c> + <see cref="GovernedTerminationInterceptor"/>), never both.
/// </summary>
[Workflow]
public sealed class WorkflowOrchestration
{
    private readonly Dictionary<string, byte[]> _events = new();

    // Raise ids already handled — the per-instance idempotency state. Signals are delivered to workflow
    // code one at a time and in the same order on every replay, so this set rebuilds deterministically.
    private readonly HashSet<string> _handledRaiseIds = new();

    private static readonly ActivityOptions ActivityOptions = new() { StartToCloseTimeout = TimeSpan.FromMinutes(1) };

    // The termination-on-failure path runs under a detached cancellation token: when a workflow is cancelled the
    // workflow's own CancellationToken is already cancelled, so an activity bound to it would be cancelled
    // before it could shred. CancellationToken.None lets the crypto-shred actually run during cancellation.
    private static readonly ActivityOptions TerminationOnFailureOptions =
        new() { StartToCloseTimeout = TimeSpan.FromMinutes(1), CancellationToken = CancellationToken.None };

    // startSequence carries the per-step sequence across continue-as-new generations so the idempotency
    // key (InstanceId, DtoType, Sequence) stays unique for the instance's whole life — a fresh generation
    // must not reuse sequence 0 and collide with the previous generation's first step.
    [WorkflowRun]
    public async Task<byte[]> Run(byte[] seed, long startSequence = 0)
    {
        byte[] current = seed;
        long sequence = startSequence;

        try
        {
            while (true)
            {
                long step = sequence++;
                WorkflowActionDto action = await Wf.ExecuteActivityAsync(
                    (WorkflowActivities a) => a.RunStep(new StepInput(current, step)), ActivityOptions);

                switch (action.Kind)
                {
                    case "complete":
                        await Wf.ExecuteActivityAsync(
                            (WorkflowActivities a) => a.Terminate(new TerminateInput(step)), ActivityOptions);
                        return action.Payload;

                    case "next":
                        current = action.Payload;
                        break;

                    case "wait":
                        current = await AwaitSignalAsync(action);
                        break;

                    case "delay":
                        await Wf.DelayAsync(TimeSpan.FromTicks(action.TimeoutTicks));
                        break;

                    case "loop":
                        throw Wf.CreateContinueAsNewException((WorkflowOrchestration wf) => wf.Run(action.Payload, sequence));

                    default:
                        throw new InvalidOperationException($"unhandled action kind: {action.Kind}");
                }
            }
        }
        catch (Exception ex) when (ex is not ContinueAsNewException)
        {
            // A failed or cancelled portable instance still owes the crypto-shred — the termination is "completion,
            // cancellation, or erasure". continue-as-new throws its own control-flow exception (excluded here) so
            // a fresh generation keeps its key; a Complete already shredded so this is an idempotent no-op.
            await Wf.ExecuteActivityAsync(
                (WorkflowActivities a) => a.Terminate(new TerminateInput(sequence)), TerminationOnFailureOptions);
            throw;
        }
    }

    private async Task<byte[]> AwaitSignalAsync(WorkflowActionDto wait)
    {
        if (wait.TimeoutTicks < 0)
        {
            await Wf.WaitConditionAsync(() => _events.ContainsKey(wait.EventName));
            return Raised(wait, Consume(wait.EventName));
        }

        bool delivered = await Wf.WaitConditionAsync(
            () => _events.ContainsKey(wait.EventName), TimeSpan.FromTicks(wait.TimeoutTicks));

        return delivered
            ? Raised(wait, Consume(wait.EventName))
            : wait.OnTimeout is { Length: > 0 } ? wait.OnTimeout
            : throw new InvalidOperationException(
                $"durable timer elapsed waiting for '{wait.EventName}' with no OnTimeout step");
    }

    // Consume the delivered signal so a later wait on the SAME event name blocks for a fresh raise instead of
    // resolving immediately off the stale entry. Deterministic/replay-safe: the removal happens in workflow
    // code, in the same order on every replay.
    private byte[]? Consume(string name)
    {
        _events.Remove(name, out byte[]? payload);
        return payload;
    }

    // A signal raised with a payload carries the next step; one raised empty resumes into the wait's sealed
    // OnEvent continuation (journaled by the step activity at wait time). With neither a payload nor an OnEvent
    // step there is nothing to resume into — throw descriptively rather than continuing with empty bytes that
    // would only fail later at decrypt (matching the InProc driver instead of diverging from it).
    private static byte[] Raised(WorkflowActionDto wait, byte[]? payload) =>
        payload is { Length: > 0 } ? payload
        : wait.OnEvent is { Length: > 0 } ? wait.OnEvent
        : throw new InvalidOperationException(
            $"'{wait.EventName}' was raised with an empty payload and the wait has no OnEvent step");

    [WorkflowSignal]
    public Task RaiseEvent(string name, byte[] payload, string? raiseId)
    {
        // Idempotent raise: a re-raise carrying an already-handled id is dropped, so it cannot deliver the
        // event a second time. A raise with no id (or a new id) falls through — distinct same-name signals
        // each count, preserving repeatable waits on one event name.
        if (raiseId is not null && !_handledRaiseIds.Add(raiseId))
        {
            return Task.CompletedTask;
        }

        _events[name] = payload;
        return Task.CompletedTask;
    }
}
