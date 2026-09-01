using Microsoft.DurableTask;

namespace SoEx.Workflow.Runtime.DurableTask;

/// <summary>
/// The portable flow's orchestration input: the sealed seed plus the per-step sequence to resume from.
/// <see cref="StartSequence"/> carries the sequence across continue-as-new generations so the idempotency
/// key (InstanceId, DtoType, Sequence) stays unique for the instance's whole life. A first start passes
/// <c>StartSequence = 0</c>.
/// </summary>
public sealed record PortableSeed(byte[] Seed, long StartSequence = 0);

/// <summary>
/// The wire format the gateway raises to the portable flow: the sealed step bytes plus an optional
/// <see cref="RaiseId"/>. A re-raise carrying an already-handled id is dropped by the orchestration so it
/// cannot deliver the event twice; a null id (or a new one) is a distinct business raise. Replacing the
/// bare <c>byte[]</c> event payload with this wrapper is the wire-format change that lets DTFx dedupe —
/// callers that raise to the portable flow (gateway or direct client) send a <see cref="RaisedEvent"/>.
/// </summary>
public sealed record RaisedEvent(string? RaiseId, byte[] Payload);

/// <summary>
/// Portable flow — the modern Durable Task orchestration, this runtime's generic driver (the
/// replay path). Drives the step loop by calling the entrypoint step as a Durable Task activity and
/// routing the returned <see cref="WorkflowAction"/> (flattened to <see cref="WorkflowActionDto"/>)
/// onto the SDK's deterministic primitives (CallActivityAsync / CreateTimer / WaitForExternalEvent /
/// ContinueAsNew). The entrypoint runs off the replay path in the activity. Use this or the
/// native flow (the consumer's own <see cref="GovernedTaskOrchestrator{TIn,TOut}"/>) per instance — never both.
/// </summary>
[DurableTask]
public sealed class WorkflowOrchestration : TaskOrchestrator<PortableSeed, byte[]>
{
    // The cross-runtime step-failure policy mapped onto Durable Task's activity retry: bounded exponential
    // backoff (was zero retries — a single transient blip faulted the orchestration). Static/deterministic, so
    // it is replay-safe. When the attempts are spent the activity call throws and the catch parks the instance.
    private static readonly TaskOptions StepOptions = DurableTaskStepOptions.From(WorkflowStepOptions.Default);

    public override async Task<byte[]> RunAsync(TaskOrchestrationContext context, PortableSeed input)
    {
        byte[] current = input.Seed;
        long sequence = input.StartSequence;

        // Raise ids already handled this generation — the per-instance idempotency state. External events
        // replay in deterministic order, so this set rebuilds identically on every replay. It resets across
        // continue-as-new (a fresh generation), matching the portable Temporal driver's per-generation dedup.
        var handledRaiseIds = new HashSet<string>();

        // External-event receivers registered but not yet consumed, keyed by event name. A multi-branch wait
        // races N receivers and only one can win; a dropped loser still consumes the next delivery for its
        // name, so its payload would vanish and the next wait on that name would park forever. Stashing the
        // losers and reusing them keeps that delivery. It is per-GENERATION (a local, rebuilt identically on
        // every replay, reset by continue-as-new) — matching handledRaiseIds and preserveUnprocessedEvents:false.
        var pendingWaits = new Dictionary<string, Task<RaisedEvent>>();

        try
        {
            while (true)
            {
                long step = sequence++;
                WorkflowActionDto action = await context.CallActivityAsync<WorkflowActionDto>(
                    nameof(StepActivity), new StepInput(current, context.InstanceId, step), StepOptions);

                switch (action.Kind)
                {
                    case "complete":
                        await context.CallActivityAsync<bool>(nameof(TerminateActivity), new TerminateInput(context.InstanceId, step));
                        return action.Payload;

                    case "next":
                        current = action.Payload;
                        break;

                    case "wait":
                        current = await AwaitEventAsync(context, action, handledRaiseIds, pendingWaits);
                        break;

                    case "delay":
                        await context.CreateTimer(TimeSpan.FromTicks(action.TimeoutTicks), CancellationToken.None);
                        break;

                    case "loop":
                        context.ContinueAsNew(new PortableSeed(action.Payload, sequence), preserveUnprocessedEvents: false);
                        return [];

                    default:
                        throw new InvalidOperationException($"unhandled action kind: {action.Kind}");
                }
            }
        }
        catch (Exception ex)
        {
            // Park-before-shred: a failed portable instance is parked with its key RETAINED, never crypto-shred
            // on the failure path — a transient step failure (or a poison step that exhausted its bounded retries)
            // must not destroy the sealed journal. continue-as-new returns above without throwing, so a fresh
            // generation keeps its key; a Complete already shredded, so the quarantine no-ops. Runs off the replay
            // path. The message is re-scrubbed against the subject index before it reaches the held log.
            await context.CallActivityAsync<bool>(nameof(QuarantineActivity),
                new QuarantineInput(context.InstanceId, sequence, WorkflowStepOptions.Default.EffectiveMaxAttempts, ex.Message));
            throw;
        }
    }

    private static async Task<byte[]> AwaitEventAsync(
        TaskOrchestrationContext context, WorkflowActionDto wait, HashSet<string> handledRaiseIds,
        Dictionary<string, Task<RaisedEvent>> pendingWaits)
    {
        WaitBranchWire[] branches = WaitBranches.Of(wait.Branches, wait.EventName, wait.OnEvent);

        Task<RaisedEvent> Pending(string name)
        {
            if (!pendingWaits.TryGetValue(name, out Task<RaisedEvent>? pending))
            {
                pendingWaits[name] = pending = context.WaitForExternalEvent<RaisedEvent>(name);
            }

            return pending;
        }

        // Compute the deadline once against the deterministic clock so a deduped duplicate doesn't reset it:
        // each pass through the loop waits only what is left of the original window.
        DateTime? deadline = wait.TimeoutTicks < 0
            ? null
            : context.CurrentUtcDateTime + TimeSpan.FromTicks(wait.TimeoutTicks);

        while (true)
        {
            if (deadline is { } due && due <= context.CurrentUtcDateTime)
            {
                return TimedOut(branches, wait);
            }

            Task<RaisedEvent>[] waits = [.. branches.Select(b => Pending(b.EventName))];
            List<Task> racing = [.. waits];

            // The timer is cancelled as soon as the race resolves, so a wait that an event won does not leave a
            // durable timer pending in the instance's history until its (possibly very distant) deadline.
            using var timerCancellation = new CancellationTokenSource();
            if (deadline is { } fireAt)
            {
                racing.Add(context.CreateTimer(fireAt, timerCancellation.Token));
            }

            await Task.WhenAny(racing);
            timerCancellation.Cancel();

            // Re-scan in DECLARED order rather than taking whichever task WhenAny surfaced: when more than one
            // of the wait's events is already deliverable, the first branch declared wins. Deterministic — the
            // scan is over a list rebuilt identically on every replay.
            int winner = -1;
            for (int i = 0; i < waits.Length; i++)
            {
                if (waits[i].IsCompleted)
                {
                    winner = i;
                    break;
                }
            }

            if (winner < 0)
            {
                return TimedOut(branches, wait);   // the durable timer won the race
            }

            RaisedEvent ev = await waits[winner];
            // Consumed: drop it from the stash so a re-loop (or a later wait on this name) registers afresh.
            // The OTHER branches keep their stashed receivers, so a duplicate does not re-register them.
            pendingWaits.Remove(branches[winner].EventName);

            if (IsDuplicate(ev, handledRaiseIds))
            {
                continue;   // accidental redelivery of an already-handled id — keep waiting for the next raise
            }

            return Raised(branches[winner], ev.Payload);
        }
    }

    private static byte[] TimedOut(WaitBranchWire[] branches, WorkflowActionDto wait) =>
        wait.OnTimeout is { Length: > 0 } ? wait.OnTimeout
        : throw new InvalidOperationException(
            $"durable timer elapsed waiting for {WaitBranches.Quoted(branches)} with no OnTimeout step");

    // A re-raise carrying an already-handled id is a duplicate; a null id (or a new one) is a distinct raise.
    private static bool IsDuplicate(RaisedEvent ev, HashSet<string> handledRaiseIds) =>
        ev.RaiseId is not null && !handledRaiseIds.Add(ev.RaiseId);

    // An event raised with a payload carries the next step; one raised empty resumes into the continuation its
    // OWN branch declared (sealed and journaled by the step activity at wait time). With neither a payload nor
    // an OnEvent step there is nothing to resume into — throw descriptively rather than continuing with empty
    // bytes that would only fail later at decrypt (matching the InProc driver instead of diverging from it).
    private static byte[] Raised(WaitBranchWire branch, byte[]? payload) =>
        payload is { Length: > 0 } ? payload
        : branch.OnEvent is { Length: > 0 } ? branch.OnEvent
        : throw new InvalidOperationException(
            $"'{branch.EventName}' was raised with an empty payload and its branch of the wait has no OnEvent step");
}
