using Elsa.Workflows;
using SoEx.Workflow;

namespace SoEx.Workflow.Runtime.Elsa;

/// <summary>
/// Native flow — Elsa termination hook. Runs the erasure lifecycle (<see cref="GovernedTermination"/>) as the final step
/// of a consumer's native Elsa flow, so on completion the per-instance key is crypto-shredded and the
/// subject index pruned. Elsa is checkpoint/resume (not replay), so this runs once as a normal step.
/// A faulted/abandoned flow that never reaches it is closed when a later erasure request for the subject
/// re-drives the still-indexed instance via <c>ErasureCoordinator.EraseAsync</c>, or by the
/// request-independent <c>ErasureCoordinator.SweepAsync</c> that ages and shreds the live key set.
/// </summary>
public sealed class GovernedTerminationActivity : Activity
{
    public required GovernedTermination Termination { get; init; }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        string instanceId = context.WorkflowExecutionContext.Id;
        await Termination.TerminateAsync(
            instanceId, new IdempotencyKey(instanceId, "terminal", 0), TerminationTrigger.NaturalCompletion);
        await context.CompleteActivityAsync();
    }
}
