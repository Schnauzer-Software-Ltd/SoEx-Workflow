using SoEx.Workflow;
using Temporalio.Activities;

namespace SoEx.Workflow.Runtime.Temporal;

public sealed record StepInput(byte[] Payload, long Sequence);

public sealed record TerminateInput(long Sequence);

/// <summary>
/// A flattened, sandbox-safe view of a <see cref="WorkflowAction"/> the workflow can
/// switch on without polymorphic ($type) deserialization on the replay path.
/// </summary>
public sealed record WorkflowActionDto(string Kind, byte[] Payload, string EventName, long TimeoutTicks, byte[] OnTimeout, byte[] OnEvent);

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
        }
        catch (Exception ex) when (!GovernedStepFailure.IsJournalSafe(step, ambient, ex))
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
            WorkflowAction.WaitForEvent w => new(
                "wait",
                [],
                step.GuardVisibleName(w.EventName, ambient),
                w.Timeout?.Ticks ?? -1,
                w.OnTimeout is { } ot ? step.SealStep(instanceId, ot, ambient) : [],
                w.OnEvent is { } oe ? step.SealStep(instanceId, oe, ambient) : []),
            WorkflowAction.Delay d => new("delay", [], "", d.Duration.Ticks, [], []),
            WorkflowAction.Loop l => new("loop", step.SealStep(instanceId, l.CarryState, ambient), "", 0, [], []),
            _ => throw new InvalidOperationException($"unhandled action {action.Kind()}"),
        };
    }

    [Activity]
    public void Terminate(TerminateInput input)
    {
        string instanceId = ActivityExecutionContext.Current.Info.WorkflowId!;
        var key = new IdempotencyKey(instanceId, "terminal", input.Sequence);
        termination.TerminateAsync(instanceId, key, TerminationTrigger.NaturalCompletion).GetAwaiter().GetResult();
    }
}
