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
/// <para>
/// The termination is resolved per run when not supplied, so this can sit in a <b>registered</b> definition
/// whose rehydrated instances hold no live object references (<see cref="ElsaWorkflowHost.BuildDurable"/>
/// registers it). The shred anchors on the <b>correlation id</b> — the logical id governance minted the key
/// under, which <see cref="ElsaWorkflowGateway"/> sets at start — because Elsa mints its own unrelated
/// instance id per create, and shredding under that would target a key that was never minted and silently
/// do nothing. A host that instead pins the Elsa instance id to the logical id (no correlation id) still
/// works via the fallback.
/// </para>
/// </summary>
public sealed class GovernedTerminationActivity : Activity
{
    /// <summary>The governed termination. Optional: falls back to the <see cref="GovernedTermination"/> registered in DI.</summary>
    public GovernedTermination? Termination { get; init; }

    /// <summary>The logical saga id governance anchors on. Optional: falls back to the correlation id, then the Elsa instance id.</summary>
    public string? SagaInstanceId { get; init; }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        GovernedTermination termination = Termination ?? context.GetRequiredService<GovernedTermination>();
        string instanceId = SagaInstanceId
            ?? (context.WorkflowExecutionContext.CorrelationId is { Length: > 0 } correlation
                ? correlation
                : context.WorkflowExecutionContext.Id);

        await termination.TerminateAsync(
            instanceId, new IdempotencyKey(instanceId, "terminal", 0), TerminationTrigger.NaturalCompletion);
        await context.CompleteActivityAsync();
    }
}
