using SoEx.Workflow;
using Temporalio.Worker;

namespace SoEx.Workflow.Temporal;

/// <summary>
/// Durable host — builds a Temporal worker that serves the portable flow (the
/// <see cref="WorkflowOrchestration"/> driver plus its <see cref="WorkflowActivities"/> step/termination
/// activities) against a live Temporal cluster. The workflow state lives in the server, so it survives
/// a worker restart; submit instances through the same client by starting <see cref="WorkflowOrchestration"/>
/// on the worker's task queue. For Tier-1 (no server, time-skipping) use <see cref="TemporalTestWorkflowHost"/>.
/// Use this or the native flow, never both for one instance.
/// </summary>
public static class TemporalWorkflowHost
{
    /// <summary>The conventional task queue, when the caller has no reason to pick another.</summary>
    public const string DefaultTaskQueue = "soex-workflow";

    /// <summary>
    /// Builds a worker hosting the portable flow on <paramref name="taskQueue"/>. Run it with
    /// <c>ExecuteAsync</c>; a fresh worker over the same client/queue resumes server-persisted instances.
    /// </summary>
    /// <param name="client">A connected client (e.g. a <c>TemporalClient</c>) — the worker uses it for polling.</param>
    public static TemporalWorker BuildWorker(
        IWorkerClient client, string taskQueue, IGovernedStep step, GovernedTermination termination)
    {
        var options = new TemporalWorkerOptions(taskQueue)
            .AddAllActivities(new WorkflowActivities(step, termination));
        options.AddWorkflow<WorkflowOrchestration>();
        return new TemporalWorker(client, options);
    }
}
