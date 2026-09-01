using Temporalio.Workflows;
using Wf = Temporalio.Workflows.Workflow;

namespace SoEx.Workflow.Runtime.Temporal;

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

    // The step activity now carries a BOUNDED retry policy mapped from the cross-runtime step options — the old
    // options had no RetryPolicy, so the server default retried a failing step forever, silently. When the
    // bounded attempts are spent the activity throws and the catch below parks the instance (park-before-shred).
    private static readonly ActivityOptions ActivityOptions = new()
    {
        StartToCloseTimeout = WorkflowStepOptions.Default.StepTimeout ?? TimeSpan.FromMinutes(1),
        RetryPolicy = TemporalStepOptions.RetryPolicyFrom(WorkflowStepOptions.Default),
    };

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
            // Cancellation/erasure vs step failure split the termination path. A cooperative cancellation (the
            // erasure path cancels the workflow) still crypto-shreds — the deliberate erasure. A STEP failure
            // that exhausted its bounded retries is instead PARKED with its key retained (park-before-shred): a
            // transient blip must not destroy the sealed journal. continue-as-new throws its own control-flow
            // exception (excluded here) so a fresh generation keeps its key; a Complete already shredded, so both
            // paths are idempotent no-ops. Both run under the detached cancellation token so the activity survives
            // the workflow's own cancellation.
            //
            // The workflow's own cancellation token is the authoritative, replay-safe signal that this is a
            // cancellation rather than a step failure — the exception the cancelled await surfaces varies, but the
            // token state is recorded in history and reconstructs identically on replay. A step failure leaves the
            // token unset, so it routes to park.
            if (Wf.CancellationToken.IsCancellationRequested)
            {
                await Wf.ExecuteActivityAsync(
                    (WorkflowActivities a) => a.Terminate(new TerminateInput(sequence)), TerminationOnFailureOptions);
            }
            else
            {
                await Wf.ExecuteActivityAsync(
                    (WorkflowActivities a) => a.Quarantine(
                        new QuarantineInput(sequence, WorkflowStepOptions.Default.EffectiveMaxAttempts, ex.Message)),
                    TerminationOnFailureOptions);
            }

            throw;
        }
    }

    private async Task<byte[]> AwaitSignalAsync(WorkflowActionDto wait)
    {
        WaitBranchWire[] branches = WaitBranches.Of(wait.Branches, wait.EventName, wait.OnEvent);
        bool AnyDelivered() => branches.Any(b => _events.ContainsKey(b.EventName));

        if (wait.TimeoutTicks < 0)
        {
            await Wf.WaitConditionAsync(AnyDelivered);
            return Resume(branches);
        }

        bool delivered = await Wf.WaitConditionAsync(AnyDelivered, TimeSpan.FromTicks(wait.TimeoutTicks));

        return delivered
            ? Resume(branches)
            : wait.OnTimeout is { Length: > 0 } ? wait.OnTimeout
            : throw new InvalidOperationException(
                $"durable timer elapsed waiting for {WaitBranches.Quoted(branches)} with no OnTimeout step");
    }

    // Which branch resumed the wait. When more than one of its signals has already been delivered, the FIRST
    // branch declared wins — a property of the flow, not of Temporal's delivery order. Deterministic: the
    // scan runs in workflow code over a list rebuilt identically on every replay.
    private byte[] Resume(WaitBranchWire[] branches)
    {
        WaitBranchWire branch = branches.First(b => _events.ContainsKey(b.EventName));
        return Raised(branch, Consume(branch.EventName));
    }

    // Consume the delivered signal so a later wait on the SAME event name blocks for a fresh raise instead of
    // resolving immediately off the stale entry. Deterministic/replay-safe: the removal happens in workflow
    // code, in the same order on every replay.
    private byte[]? Consume(string name)
    {
        _events.Remove(name, out byte[]? payload);
        return payload;
    }

    // A signal raised with a payload carries the next step; one raised empty resumes into the continuation its
    // OWN branch declared (sealed and journaled by the step activity at wait time). With neither a payload nor
    // an OnEvent step there is nothing to resume into — throw descriptively rather than continuing with empty
    // bytes that would only fail later at decrypt (matching the InProc driver instead of diverging from it).
    private static byte[] Raised(WaitBranchWire branch, byte[]? payload) =>
        payload is { Length: > 0 } ? payload
        : branch.OnEvent is { Length: > 0 } ? branch.OnEvent
        : throw new InvalidOperationException(
            $"'{branch.EventName}' was raised with an empty payload and its branch of the wait has no OnEvent step");

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
