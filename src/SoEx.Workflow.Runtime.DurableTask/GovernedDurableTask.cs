using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using SoEx.Workflow;

namespace SoEx.Workflow.Runtime.DurableTask;

/// <summary>
/// Native flow — base orchestrator that runs the erasure lifecycle automatically when a consumer's native Durable
/// Task orchestration completes (the per-backend termination hook). The consumer overrides <see cref="Flow"/>
/// with their native flow (CallActivity / CreateTimer / WaitForExternalEvent / ContinueAsNew); the base
/// wraps it in a finally that schedules the termination activity — the key-store mutation runs off the
/// replay path as an activity. Covers completion and the timeout/compensation path; management
/// terminate/purge bypasses orchestrator code (closed when a later erasure request for the subject
/// re-drives the still-indexed instance, or by the request-independent <c>ErasureCoordinator.SweepAsync</c>
/// that ages and shreds the live key set).
/// </summary>
public abstract class GovernedTaskOrchestrator<TIn, TOut> : TaskOrchestrator<TIn, TOut>
{
    public sealed override async Task<TOut> RunAsync(TaskOrchestrationContext context, TIn input)
    {
        // The native flow receives a context that watches for continue-as-new. Unlike Temporal, DTFx signals
        // continue-as-new by a normal return (no control-flow exception), so a finally would shred the key on
        // every generation boundary — leaving the carried sealed seed undecryptable. Observing the call lets a
        // CAN generation keep its key while every termination (completion/failure/cancel) still shreds.
        var watched = new ContinuationWatchingContext(context);
        try
        {
            return await Flow(watched, input);
        }
        finally
        {
            if (!watched.ContinuedAsNew)
            {
                await context.CallActivityAsync<bool>(nameof(GovernedTerminationActivity), context.InstanceId);
            }
        }
    }

    protected abstract Task<TOut> Flow(TaskOrchestrationContext context, TIn input);

    /// <summary>
    /// A pass-through <see cref="TaskOrchestrationContext"/> that records when the native flow requests
    /// continue-as-new, so <see cref="RunAsync"/> can skip the termination shred on a generation boundary
    /// without the consumer having to call a special method. Every other member delegates to the underlying context.
    /// </summary>
    private sealed class ContinuationWatchingContext(TaskOrchestrationContext inner) : TaskOrchestrationContext
    {
        public bool ContinuedAsNew { get; private set; }

        public override void ContinueAsNew(object? newInput = null, bool preserveUnprocessedEvents = true)
        {
            ContinuedAsNew = true;
            inner.ContinueAsNew(newInput, preserveUnprocessedEvents);
        }

        public override TaskName Name => inner.Name;
        public override string InstanceId => inner.InstanceId;
        public override ParentOrchestrationInstance? Parent => inner.Parent;
        public override DateTime CurrentUtcDateTime => inner.CurrentUtcDateTime;
        public override bool IsReplaying => inner.IsReplaying;
        protected override ILoggerFactory LoggerFactory => inner.ReplaySafeLoggerFactory;
        public override ILoggerFactory ReplaySafeLoggerFactory => inner.ReplaySafeLoggerFactory;
        public override T? GetInput<T>() where T : default => inner.GetInput<T>();
        public override Task<TResult> CallActivityAsync<TResult>(TaskName name, object? input = null, TaskOptions? options = null) =>
            inner.CallActivityAsync<TResult>(name, input, options);
        public override Task CreateTimer(DateTime fireAt, CancellationToken cancellationToken) =>
            inner.CreateTimer(fireAt, cancellationToken);
        public override Task<T> WaitForExternalEvent<T>(string eventName, CancellationToken cancellationToken = default) =>
            inner.WaitForExternalEvent<T>(eventName, cancellationToken);
        public override void SendEvent(string instanceId, string eventName, object payload) =>
            inner.SendEvent(instanceId, eventName, payload);
        public override void SetCustomStatus(object? customStatus) => inner.SetCustomStatus(customStatus);
        public override Task<TResult> CallSubOrchestratorAsync<TResult>(TaskName orchestratorName, object? input = null, TaskOptions? options = null) =>
            inner.CallSubOrchestratorAsync<TResult>(orchestratorName, input, options);
        public override Guid NewGuid() => inner.NewGuid();
    }
}

/// <summary>The termination erasure lifecycle as a Durable Task activity (off the replay path).</summary>
public sealed class GovernedTerminationActivity(GovernedTermination termination) : TaskActivity<string, bool>
{
    public override async Task<bool> RunAsync(TaskActivityContext context, string instanceId)
    {
        await termination.TerminateAsync(
            instanceId, new IdempotencyKey(instanceId, "terminal", 0), TerminationTrigger.NaturalCompletion);
        return true;
    }
}
