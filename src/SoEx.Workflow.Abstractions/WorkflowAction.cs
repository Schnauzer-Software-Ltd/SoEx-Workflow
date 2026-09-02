namespace SoEx.Workflow;

/// <summary>
/// The control directive a workflow step-handler returns. The generic
/// orchestration driver executes it against the runtime; the step-handler never
/// calls the runtime. The hierarchy is closed to the variants declared here.
/// </summary>
public abstract record WorkflowAction
{
    private WorkflowAction() { }

    private readonly IReadOnlyList<string> _subjects = [];

    /// <summary>
    /// Subjects this step learned while it ran, to enroll on the instance — the people the flow discovered
    /// rather than the one it was started for. The step is what knows them (a lookup returned a partner, a
    /// claim named a dependant), so the declaration rides its return value.
    /// <para>
    /// The framework folds them into the step's subject context before it flattens this action: it indexes
    /// them at once, so an erasure request reaches the instance even while it sits parked at a wait, and it
    /// carries them onto the sealed continuation, so from here on they are guarded out of every name the
    /// runtime journals in clear. They never appear in the flattened action itself, which is journaled — only
    /// in the sealed step, which the crypto-shred can reach.
    /// </para>
    /// <para>
    /// Declare them on an action that continues the flow. A <see cref="Delay"/> seals no next step for them to
    /// travel on and rejects a non-empty set rather than half-applying it. The guard is prospective: names
    /// already journaled for this instance were checked against the subjects known at the time, and enrolling
    /// one now does not re-examine them.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Subjects
    {
        get => _subjects;

        // Normalized to empty rather than null on the way in: an action round-trips through the host message
        // serializer as a polymorphic response, and a JSON null here would otherwise reach the drivers as a
        // null reference on every action, not only one that declared a subject.
        init => _subjects = value ?? [];
    }

    /// <summary>The workflow is finished; <paramref name="Result"/> is the typed result the consumer returns.</summary>
    public sealed record Complete(object? Result) : WorkflowAction;

    /// <summary>
    /// Park until one of the wait's named events is raised. With a <see cref="WaitForEvent.Timeout"/>, the
    /// events race a durable timer; if the timer wins, the workflow resumes into
    /// <see cref="WaitForEvent.OnTimeout"/> (e.g. a compensation step DTO). Each branch carries its own
    /// <see cref="EventBranch.OnEvent"/> continuation, so the flow decides at wait time what a bare
    /// (payload-free) raise of that branch means: a caller can then raise just the instance id + event
    /// name with no payload (and no flow knowledge) and the driver resumes into that branch's journaled
    /// continuation; an event raised <i>with</i> a payload still wins, carrying event data into the next
    /// step as before. The framework envelopes the typed steps — the consumer returns DTOs, not bytes.
    /// <para>
    /// Branch order is significant: it is the tie-break when more than one of the wait's events is
    /// already deliverable at wait time, so the first branch declared wins. That makes the choice
    /// deterministic (and so replay-safe) rather than a race between the runtime's delivery order.
    /// </para>
    /// </summary>
    public sealed record WaitForEvent : WorkflowAction
    {
        /// <summary>
        /// A wait over one or more branches, each resumable by its own event, all racing the timer.
        /// <para>
        /// This is deliberately the type's ONLY constructor. A <see cref="WorkflowAction"/> round-trips
        /// through the host's message serializer (the step operation returns it as a polymorphic response),
        /// and a second public constructor leaves the serializer no unambiguous way to rebuild the value —
        /// so the single-name convenience overload that would otherwise live here cannot exist. It also
        /// leaves the wait with one internal representation, which is what keeps every adapter reading a
        /// wait the same way.
        /// </para>
        /// </summary>
        public WaitForEvent(IReadOnlyList<EventBranch> Branches, TimeSpan? Timeout = null, object? OnTimeout = null)
        {
            ArgumentNullException.ThrowIfNull(Branches);
            if (Branches.Count == 0)
            {
                throw new ArgumentException("a wait needs at least one event branch", nameof(Branches));
            }

            // A duplicate name is unresolvable, not merely redundant: the event NAME is the delivery key on
            // every runtime (signal name / external-event name / bookmark name / durable-promise key), so two
            // branches sharing one could never be told apart at resume. Reject it at the flow, where the
            // author can see it, rather than letting one branch silently shadow the other at runtime.
            for (int i = 0; i < Branches.Count; i++)
            {
                if (string.IsNullOrEmpty(Branches[i].EventName))
                {
                    throw new ArgumentException("an event branch needs a name", nameof(Branches));
                }

                for (int j = i + 1; j < Branches.Count; j++)
                {
                    if (string.Equals(Branches[i].EventName, Branches[j].EventName, StringComparison.Ordinal))
                    {
                        throw new ArgumentException(
                            $"'{Branches[i].EventName}' appears on more than one branch of the same wait — the event name is the delivery key, so duplicate branches cannot be told apart",
                            nameof(Branches));
                    }
                }
            }

            // Copied into a plain array, not a collection-expression target of the IReadOnlyList property:
            // that would synthesise the compiler's internal read-only-array type, which the host serializer
            // records as the payload's $type and then cannot reconstruct on the way back in. Copying also
            // keeps the caller from mutating the list behind the guards above.
            EventBranch[] copy = [.. Branches];
            this.Branches = copy;
            this.Timeout = Timeout;
            this.OnTimeout = OnTimeout;
        }

        /// <summary>The branches this wait can be resumed by, in declared (tie-break) order. Never empty.</summary>
        public IReadOnlyList<EventBranch> Branches { get; }

        /// <summary>How long the events race a durable timer, or null to park indefinitely.</summary>
        public TimeSpan? Timeout { get; }

        /// <summary>The typed step DTO to resume into when the durable timer wins the race.</summary>
        public object? OnTimeout { get; }
    }

    /// <summary>Park on a durable timer for <paramref name="Duration"/>.</summary>
    public sealed record Delay(TimeSpan Duration) : WorkflowAction;

    /// <summary>Route the typed <paramref name="NextStep"/> DTO into the next step (the framework envelopes it).</summary>
    public sealed record RaiseIntoNext(object NextStep) : WorkflowAction;

    /// <summary>Continue-as-new, carrying the typed <paramref name="CarryState"/> DTO across the boundary.</summary>
    public sealed record Loop(object CarryState) : WorkflowAction;
}

/// <summary>
/// One resumable branch of a <see cref="WorkflowAction.WaitForEvent"/>: the event name that resumes it,
/// plus the typed step DTO a bare (payload-free) raise of that name resumes into. <paramref name="OnEvent"/>
/// is sealed and journaled at wait time, so a context-free caller needs nothing but the instance id and the
/// event name; a raise carrying a payload supplies the next step itself and wins over it.
/// </summary>
public sealed record EventBranch(string EventName, object? OnEvent = null);

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

    /// <summary>
    /// Wraps a typed next-step DTO as a <see cref="WorkflowAction.RaiseIntoNext"/> — the portable flow's
    /// "route this step into the next step now" directive. Sugar for
    /// <c>new WorkflowAction.RaiseIntoNext(nextStep)</c>, read as <c>nextStep.Raise()</c>.
    /// </summary>
    public static WorkflowAction Raise(this object nextStep) => new WorkflowAction.RaiseIntoNext(nextStep);

    /// <summary>
    /// Declares subjects the step just learned on the action it returns, read as
    /// <c>nextStep.Raise().Enrolling(partnerEmail)</c>. Sugar for <c>action with { Subjects = [...] }</c>;
    /// see <see cref="WorkflowAction.Subjects"/> for what the framework does with them.
    /// </summary>
    public static WorkflowAction Enrolling(this WorkflowAction action, params string[] subjects) =>
        action with { Subjects = subjects };
}
