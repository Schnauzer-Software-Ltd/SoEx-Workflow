using Microsoft.DurableTask;
using SoEx.Workflow;

namespace SoEx.Workflow.Runtime.DurableTask;

public sealed record StepInput(byte[] Payload, string InstanceId, long Sequence);

public sealed record TerminateInput(string InstanceId, long Sequence);

public sealed record QuarantineInput(string InstanceId, long Sequence, int Attempts, string Error);

/// <summary>Maps the cross-runtime <see cref="WorkflowStepOptions"/> onto the Durable Task activity retry primitive.</summary>
internal static class DurableTaskStepOptions
{
    public static TaskOptions From(WorkflowStepOptions options) =>
        TaskOptions.FromRetryPolicy(new RetryPolicy(
            maxNumberOfAttempts: options.EffectiveMaxAttempts,
            firstRetryInterval: options.FirstRetryDelay,
            backoffCoefficient: options.BackoffCoefficient,
            maxRetryInterval: options.MaxRetryDelay));
}

/// <summary>A flattened, replay-safe view of a <see cref="WorkflowAction"/> for the orchestration.</summary>
public sealed record WorkflowActionDto(
    string Kind, byte[] Payload, string EventName, long TimeoutTicks, byte[] OnTimeout, byte[] OnEvent,
    WaitBranchWire[]? Branches = null)
{
    /// <summary>
    /// The wait's branches, in declared order. Normalised to empty rather than null because this DTO is
    /// journaled and, for Restate, crosses a language boundary: the contract on that wire is that an absent
    /// value is the empty collection, never JSON <c>null</c>. A serialized null decodes as a type error in
    /// the Rust sidecar, which fails the invocation on EVERY action, not just a wait.
    /// </summary>
    public WaitBranchWire[] Branches { get; init; } = Branches ?? [];
}

/// <summary>
/// Portable flow — runs one entrypoint step as a Durable Task activity (off the replay path)
/// through the governed-step core (<see cref="IGovernedStep"/>) and returns the flattened action for the
/// orchestration to route. The component's step operation returns a <see cref="WorkflowAction"/>.
/// </summary>
public sealed class StepActivity(IGovernedStep step)
    : TaskActivity<StepInput, WorkflowActionDto>
{
    public override async Task<WorkflowActionDto> RunAsync(TaskActivityContext context, StepInput input)
    {
        // Compute the ambient and guard the in-clear instance id (the scheduler journals it; the framework
        // cannot intercept the submit) INSIDE the try, so a throw here is scrubbed by the same catch instead of
        // faulting the orchestration with an in-clear message.
        byte[]? ambient = null;
        WorkflowAction action;
        try
        {
            ambient = step.AmbientOf(input.InstanceId, input.Payload);
            step.GuardVisibleName(input.InstanceId, ambient);
            action = (await step.DispatchGovernedAsync(input.Payload, input.InstanceId, input.Sequence)) as WorkflowAction
                ?? throw new InvalidOperationException($"the '{step.OperationName}' operation did not return a {nameof(WorkflowAction)}");

            // Fold in whatever this step enrolled before the flattening below guards or seals anything. Inside
            // the try, so a rejected enrollment is scrubbed by the same catch rather than faulting the
            // orchestration with an unscrubbed message.
            ambient = step.EnrollSubjects(input.InstanceId, ambient, action);
        }
        catch (Exception ex) when (!GovernedStepFailure.IsJournalSafe(step, input.InstanceId, ambient, ex))
        {
            // The activity-failure message lands in the orchestration history in clear and survives the shred,
            // so a step exception carrying a subject id is replaced (never chained — ToString would leak it).
            throw new InvalidOperationException(GovernedStepFailure.WithheldMessage);
        }

        return action switch
        {
            WorkflowAction.Complete c => new("complete", step.GuardResultPiiFree(step.Serializer.Serialize(c.Result), ambient), "", 0, [], []),
            WorkflowAction.RaiseIntoNext r => new("next", step.SealStep(input.InstanceId, r.NextStep, ambient), "", 0, [], []),
            WorkflowAction.WaitForEvent w => WaitFlattening.Wait(step, input.InstanceId, w, ambient),
            WorkflowAction.Delay d => new("delay", [], "", d.Duration.Ticks, [], []),
            WorkflowAction.Loop l => new("loop", step.SealStep(input.InstanceId, l.CarryState, ambient), "", 0, [], []),
            _ => throw new InvalidOperationException($"unhandled action {action.Kind()}"),
        };
    }
}

/// <summary>
/// Flattens a wait for the orchestration. Branch 0 is repeated in the legacy EventName/OnEvent fields so a
/// single-branch wait journaled here replays unchanged if this deploy is rolled back; WaitBranches.Of reads
/// the other direction, for an instance already parked at a wait when this code was deployed over it.
/// </summary>
internal static class WaitFlattening
{
    public static WorkflowActionDto Wait(IGovernedStep step, string instanceId, WorkflowAction.WaitForEvent w, byte[]? ambient)
    {
        WaitBranchWire[] branches = WaitBranches.Flatten(step, instanceId, w, ambient);
        return new(
            "wait",
            [],
            branches[0].EventName,
            w.Timeout?.Ticks ?? -1,
            w.OnTimeout is { } ot ? step.SealStep(instanceId, ot, ambient) : [],
            branches[0].OnEvent,
            branches);
    }
}

/// <summary>Portable flow — runs the termination erasure lifecycle as a Durable Task activity (off the replay path).</summary>
public sealed class TerminateActivity(GovernedTermination termination)
    : TaskActivity<TerminateInput, bool>
{
    public override async Task<bool> RunAsync(TaskActivityContext context, TerminateInput input)
    {
        var key = new IdempotencyKey(input.InstanceId, "terminal", input.Sequence);
        await termination.TerminateAsync(input.InstanceId, key, TerminationTrigger.NaturalCompletion);
        return true;
    }
}

/// <summary>
/// Portable flow — parks a FAILED instance (park-before-shred) as a Durable Task activity: the key is retained,
/// the instance recorded held, never crypto-shred on the failure path. Runs off the replay path. The error text
/// is re-scrubbed against the subject index before it reaches the held log.
/// </summary>
public sealed class QuarantineActivity(GovernedTermination termination)
    : TaskActivity<QuarantineInput, bool>
{
    public override async Task<bool> RunAsync(TaskActivityContext context, QuarantineInput input)
    {
        var key = new IdempotencyKey(input.InstanceId, "terminal", input.Sequence);
        await termination.QuarantineAsync(input.InstanceId, key, input.Attempts, new Exception(input.Error));
        return true;
    }
}
