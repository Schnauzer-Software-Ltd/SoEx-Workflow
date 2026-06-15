namespace SoEx.Workflow;

/// <summary>
/// The control directive a workflow step-handler returns. The generic
/// orchestration driver executes it against the runtime; the step-handler never
/// calls the runtime. The hierarchy is closed to the variants declared here.
/// </summary>
public abstract record WorkflowAction
{
    private WorkflowAction() { }

    /// <summary>The workflow is finished; <paramref name="Result"/> is the typed result the consumer returns.</summary>
    public sealed record Complete(object? Result) : WorkflowAction;

    /// <summary>
    /// Park until the named event is raised. With a <paramref name="Timeout"/>, the
    /// driver races the event against a durable timer; if the timer wins, the workflow
    /// resumes into <paramref name="OnTimeout"/> (e.g. a compensation step DTO). With an
    /// <paramref name="OnEvent"/> continuation, the flow decides at wait time what an
    /// empty-payload event means: a caller can then raise just the instance id + event
    /// name with no payload (and no flow knowledge) and the driver resumes into the
    /// journaled <paramref name="OnEvent"/> step; an event raised <i>with</i> a payload
    /// still wins, carrying event data into the next step as before. The framework
    /// envelopes the typed steps — the consumer returns DTOs, not bytes.
    /// </summary>
    public sealed record WaitForEvent(string EventName, TimeSpan? Timeout = null, object? OnTimeout = null, object? OnEvent = null) : WorkflowAction;

    /// <summary>Park on a durable timer for <paramref name="Duration"/>.</summary>
    public sealed record Delay(TimeSpan Duration) : WorkflowAction;

    /// <summary>Route the typed <paramref name="NextStep"/> DTO into the next step (the framework envelopes it).</summary>
    public sealed record RaiseIntoNext(object NextStep) : WorkflowAction;

    /// <summary>Continue-as-new, carrying the typed <paramref name="CarryState"/> DTO across the boundary.</summary>
    public sealed record Loop(object CarryState) : WorkflowAction;
}

/// <summary>The kinds of <see cref="WorkflowAction"/>, one per variant.</summary>
public enum WorkflowActionKind
{
    Complete,
    WaitForEvent,
    Delay,
    RaiseIntoNext,
    Loop,
}

public static class WorkflowActionExtensions
{
    /// <summary>
    /// Maps an action to its <see cref="WorkflowActionKind"/>. The switch is
    /// exhaustive over the closed hierarchy — a new variant breaks this method
    /// until it is handled.
    /// </summary>
    public static WorkflowActionKind Kind(this WorkflowAction action) => action switch
    {
        WorkflowAction.Complete => WorkflowActionKind.Complete,
        WorkflowAction.WaitForEvent => WorkflowActionKind.WaitForEvent,
        WorkflowAction.Delay => WorkflowActionKind.Delay,
        WorkflowAction.RaiseIntoNext => WorkflowActionKind.RaiseIntoNext,
        WorkflowAction.Loop => WorkflowActionKind.Loop,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action.GetType().Name),
    };
}
