using SoEx.Workflow;
using Temporalio.Activities;

namespace SoEx.Workflow.Runtime.Temporal;

public sealed record StepInput(byte[] Payload, long Sequence);

public sealed record TerminateInput(long Sequence);

public sealed record QuarantineInput(long Sequence, int Attempts, string Error);

/// <summary>Maps the cross-runtime <see cref="WorkflowStepOptions"/> onto Temporal's activity retry primitive.</summary>
internal static class TemporalStepOptions
{
    public static Temporalio.Common.RetryPolicy RetryPolicyFrom(WorkflowStepOptions o) => new()
    {
        MaximumAttempts = o.EffectiveMaxAttempts,
        InitialInterval = o.FirstRetryDelay,
        BackoffCoefficient = (float)o.BackoffCoefficient,
        MaximumInterval = o.MaxRetryDelay,
    };
}

/// <summary>
/// A flattened, sandbox-safe view of a <see cref="WorkflowAction"/> the workflow can
/// switch on without polymorphic ($type) deserialization on the replay path.
/// </summary>
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
/// Portable flow — the entrypoint step (+ governance) and the termination erasure lifecycle, run as
/// Temporal activities off the replay path through the governed-step core. Registered as an instance
/// closing over <see cref="IGovernedStep"/> + <see cref="GovernedTermination"/>.
/// </summary>
public sealed class WorkflowActivities(IGovernedStep step, GovernedTermination termination)
{
    [Activity]
    public WorkflowActionDto RunStep(StepInput input)
    {
        string instanceId = ActivityExecutionContext.Current.Info.WorkflowId!;
        byte[]? ambient = step.AmbientOf(instanceId, input.Payload);

        WorkflowAction action;
        try
        {
            action = (step.DispatchGovernedAsync(input.Payload, instanceId, input.Sequence).GetAwaiter().GetResult()) as WorkflowAction
                ?? throw new InvalidOperationException($"the '{step.OperationName}' operation did not return a {nameof(WorkflowAction)}");

            // Fold in whatever this step enrolled before the flattening below guards or seals anything. Inside
            // the try, so a rejected enrollment is scrubbed by the same catch rather than recorded in history.
            ambient = step.EnrollSubjects(instanceId, ambient, action);
        }
        catch (Exception ex) when (!GovernedStepFailure.IsJournalSafe(step, instanceId, ambient, ex))
        {
            // Temporal records the activity-failure message in workflow history in clear, so a step exception
            // carrying a subject id would survive the shred — replace it (the original is never chained; its
            // message would leak through ToString). A PII-free message is left to propagate for diagnosability.
            throw new InvalidOperationException(GovernedStepFailure.WithheldMessage);
        }

        return action switch
        {
            WorkflowAction.Complete c => new("complete", step.GuardResultPiiFree(step.Serializer.Serialize(c.Result), ambient), "", 0, [], []),
            WorkflowAction.RaiseIntoNext r => new("next", step.SealStep(instanceId, r.NextStep, ambient), "", 0, [], []),
            WorkflowAction.WaitForEvent w => Wait(step, instanceId, w, ambient),
            WorkflowAction.Delay d => new("delay", [], "", d.Duration.Ticks, [], []),
            WorkflowAction.Loop l => new("loop", step.SealStep(instanceId, l.CarryState, ambient), "", 0, [], []),
            _ => throw new InvalidOperationException($"unhandled action {action.Kind()}"),
        };
    }

    // Branch 0 is repeated in the legacy EventName/OnEvent fields so a single-branch wait journaled here
    // replays unchanged if this deploy is rolled back; WaitBranches.Of reads the other direction, for an
    // instance already parked at a wait when this code was deployed over it.
    private static WorkflowActionDto Wait(IGovernedStep step, string instanceId, WorkflowAction.WaitForEvent w, byte[]? ambient)
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

    [Activity]
    public void Terminate(TerminateInput input)
    {
        string instanceId = ActivityExecutionContext.Current.Info.WorkflowId!;
        var key = new IdempotencyKey(instanceId, "terminal", input.Sequence);
        termination.TerminateAsync(instanceId, key, TerminationTrigger.NaturalCompletion).GetAwaiter().GetResult();
    }

    /// <summary>Park a FAILED instance (park-before-shred): retain the key, record it held. The error is re-scrubbed before the held log.</summary>
    [Activity]
    public void Quarantine(QuarantineInput input)
    {
        string instanceId = ActivityExecutionContext.Current.Info.WorkflowId!;
        var key = new IdempotencyKey(instanceId, "terminal", input.Sequence);
        termination.QuarantineAsync(instanceId, key, input.Attempts, new Exception(input.Error)).GetAwaiter().GetResult();
    }
}
