using SoEx.Workflow;
using Temporalio.Activities;
using Temporalio.Worker.Interceptors;
using Temporalio.Workflows;
using Wf = Temporalio.Workflows.Workflow;

namespace SoEx.Workflow.Runtime.Temporal;

/// <summary>
/// Native flow — the Temporal termination hook. Runs the erasure lifecycle (<see cref="GovernedTermination"/>)
/// automatically when a consumer's native workflow completes, so a backend-native flow still gets
/// "OnRetaining on every termination path -> crypto-shred" without consumer discipline. It wraps the
/// workflow run and, in a finally, schedules a termination activity (the key-store mutation is
/// non-deterministic, so it must run off the replay path as an activity).
///
/// Covers normal completion and the timeout/compensation path. Admin <c>terminate</c> bypasses
/// workflow code entirely — closed when a later erasure request for the subject re-drives the still-indexed
/// instance via <c>ErasureCoordinator.EraseAsync</c>, or by the request-independent
/// <c>ErasureCoordinator.SweepAsync</c> that ages and shreds the live key set.
/// </summary>
public sealed class GovernedTerminationInterceptor : IWorkerInterceptor
{
    public WorkflowInboundInterceptor InterceptWorkflow(WorkflowInboundInterceptor nextInterceptor) =>
        new Inbound(nextInterceptor);

    private sealed class Inbound(WorkflowInboundInterceptor next) : WorkflowInboundInterceptor(next)
    {
        // The termination activity runs under a detached cancellation token: when a native workflow is cancelled
        // its own CancellationToken is already cancelled, so an activity bound to it would be cancelled before
        // it could shred. CancellationToken.None lets the crypto-shred actually run during cancellation — the
        // same fix the portable flow applies (WorkflowOrchestration.TerminationOnFailureOptions).
        private static readonly ActivityOptions TerminationOptions =
            new() { StartToCloseTimeout = TimeSpan.FromMinutes(1), CancellationToken = CancellationToken.None };

        public override async Task<object?> ExecuteWorkflowAsync(ExecuteWorkflowInput input)
        {
            try
            {
                object? result = await base.ExecuteWorkflowAsync(input);
                await RunTerminationAsync();
                return result;
            }
            catch (Exception ex) when (ex is not ContinueAsNewException)
            {
                // A failed or cancelled native instance still owes the crypto-shred. continue-as-new throws its
                // own control-flow exception (excluded here) so the fresh generation keeps its key — without the
                // exclusion the termination would fire between generations and the carried sealed seed could not
                // decrypt. A normal completion already shredded above, so this is reached only on failure/cancel.
                await RunTerminationAsync();
                throw;
            }
        }

        private static Task RunTerminationAsync() => Wf.ExecuteActivityAsync(
            (GovernedTerminationActivities a) => a.RunTermination(Wf.Info.WorkflowId), TerminationOptions);
    }
}

/// <summary>The termination erasure lifecycle as a Temporal activity (off the replay path). Registered on
/// the worker; the <see cref="GovernedTerminationInterceptor"/> schedules it at workflow completion.</summary>
public sealed class GovernedTerminationActivities(GovernedTermination termination)
{
    [Activity]
    public Task RunTermination(string instanceId) =>
        termination.TerminateAsync(
            instanceId, new IdempotencyKey(instanceId, "terminal", 0), TerminationTrigger.NaturalCompletion);
}
