using Microsoft.DurableTask;

namespace SoEx.Workflow.DurableTask;

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
    public override async Task<byte[]> RunAsync(TaskOrchestrationContext context, PortableSeed input)
    {
        byte[] current = input.Seed;
        long sequence = input.StartSequence;

        // Raise ids already handled this generation — the per-instance idempotency state. External events
        // replay in deterministic order, so this set rebuilds identically on every replay. It resets across
        // continue-as-new (a fresh generation), matching the portable Temporal driver's per-generation dedup.
        var handledRaiseIds = new HashSet<string>();

        try
        {
            while (true)
            {
                long step = sequence++;
                WorkflowActionDto action = await context.CallActivityAsync<WorkflowActionDto>(
                    nameof(StepActivity), new StepInput(current, context.InstanceId, step));

                switch (action.Kind)
                {
                    case "complete":
                        await context.CallActivityAsync<bool>(nameof(TerminateActivity), new TerminateInput(context.InstanceId, step));
                        return action.Payload;

                    case "next":
                        current = action.Payload;
                        break;

                    case "wait":
                        current = await AwaitEventAsync(context, action, handledRaiseIds);
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
        catch
        {
            // A failed portable instance still owes the crypto-shred — mirrors the native GovernedTaskOrchestrator's
            // finally. continue-as-new returns above without throwing, so a fresh generation keeps its key; a
            // Complete already shredded, so the idempotent termination no-ops. The shred runs off the replay path.
            await context.CallActivityAsync<bool>(nameof(TerminateActivity), new TerminateInput(context.InstanceId, sequence));
            throw;
        }
    }

    private static async Task<byte[]> AwaitEventAsync(
        TaskOrchestrationContext context, WorkflowActionDto wait, HashSet<string> handledRaiseIds)
    {
        if (wait.TimeoutTicks < 0)
        {
            while (true)
            {
                RaisedEvent ev = await context.WaitForExternalEvent<RaisedEvent>(wait.EventName);
                if (IsDuplicate(ev, handledRaiseIds))
                {
                    continue;   // accidental redelivery of an already-handled id — keep waiting for the next raise
                }

                return Raised(wait, ev.Payload);
            }
        }

        // Recompute the remaining timeout against the deterministic clock so a deduped duplicate doesn't reset
        // the deadline: each loop waits only what's left of the original window.
        DateTime deadline = context.CurrentUtcDateTime + TimeSpan.FromTicks(wait.TimeoutTicks);
        while (true)
        {
            TimeSpan remaining = deadline - context.CurrentUtcDateTime;
            if (remaining <= TimeSpan.Zero)
            {
                return wait.OnTimeout is { Length: > 0 } ? wait.OnTimeout
                    : throw new InvalidOperationException(
                        $"durable timer elapsed waiting for '{wait.EventName}' with no OnTimeout step");
            }

            try
            {
                RaisedEvent ev = await context.WaitForExternalEvent<RaisedEvent>(wait.EventName, remaining);
                if (IsDuplicate(ev, handledRaiseIds))
                {
                    continue;
                }

                return Raised(wait, ev.Payload);
            }
            catch (TaskCanceledException)
            {
                // the durable timer won the race
                return wait.OnTimeout is { Length: > 0 } ? wait.OnTimeout
                    : throw new InvalidOperationException(
                        $"durable timer elapsed waiting for '{wait.EventName}' with no OnTimeout step");
            }
        }
    }

    // A re-raise carrying an already-handled id is a duplicate; a null id (or a new one) is a distinct raise.
    private static bool IsDuplicate(RaisedEvent ev, HashSet<string> handledRaiseIds) =>
        ev.RaiseId is not null && !handledRaiseIds.Add(ev.RaiseId);

    // An event raised with a payload carries the next step; one raised empty resumes into the wait's sealed
    // OnEvent continuation (journaled by the step activity at wait time). With neither a payload nor an OnEvent
    // step there is nothing to resume into — throw descriptively rather than continuing with empty bytes that
    // would only fail later at decrypt (matching the InProc driver instead of diverging from it).
    private static byte[] Raised(WorkflowActionDto wait, byte[]? payload) =>
        payload is { Length: > 0 } ? payload
        : wait.OnEvent is { Length: > 0 } ? wait.OnEvent
        : throw new InvalidOperationException(
            $"'{wait.EventName}' was raised with an empty payload and the wait has no OnEvent step");
}
