using XState;

namespace SoEx.Workflow.Statecharts;

/// <summary>
/// Work a chart invokes and then branches on: the implementation behind an <c>invoke</c> whose <c>src</c>
/// names it. Register it as the chart's actor source, under the name the chart uses:
/// <c>.ActorLogic("CreateUser", StatechartCall.Of&lt;JsonElement&gt;(...))</c>.
/// <para>
/// The step runs the call when the machine enters the invoking state, inside the same governed step, and
/// feeds the result back to the machine. A returned value takes the invoke's <c>onDone</c> transitions, with
/// the value as the event's output. A <see cref="StatechartCallFailedException"/> takes its <c>onError</c>
/// transitions. Any other exception fails the step, so the runtime retries it.
/// </para>
/// <para>
/// This is how a chart chooses its next state from a call's result. A guard on <c>onDone</c> reads the
/// output, so it decides after the call has answered. An entry action cannot do this: the machine selects
/// its transitions before it hands any action to the step.
/// </para>
/// </summary>
public sealed class StatechartCall : IActorLogic
{
    private readonly Func<CallContext, CancellationToken, Task<object?>> _run;

    private StatechartCall(Func<CallContext, CancellationToken, Task<object?>> run) => _run = run;

    /// <summary>A call that returns an output for <c>onDone</c> to branch on.</summary>
    public static StatechartCall Of<TContext>(Func<CallArgs<TContext>, CancellationToken, Task<object?>> run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new StatechartCall((call, ct) => run(Typed<TContext>(call), ct));
    }

    /// <summary>
    /// A call that returns an output and uses the services the step was given: the
    /// <c>services</c> argument of <see cref="StatechartStep.AdvanceAsync"/>. Use it to reach the components
    /// the Manager received for this call.
    /// </summary>
    public static StatechartCall Of<TContext, TServices>(
        Func<CallArgs<TContext>, TServices, CancellationToken, Task<object?>> run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new StatechartCall((call, ct) => run(Typed<TContext>(call), ServicesOf<TServices>(call.Services), ct));
    }

    /// <summary>A call with no output that uses the services the step was given.</summary>
    public static StatechartCall Of<TContext, TServices>(Func<CallArgs<TContext>, TServices, CancellationToken, Task> run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new StatechartCall(async (call, ct) =>
        {
            await run(Typed<TContext>(call), ServicesOf<TServices>(call.Services), ct).ConfigureAwait(false);
            return null;
        });
    }

    internal static TServices ServicesOf<TServices>(object? services) =>
        services is TServices typed
            ? typed
            : throw new InvalidOperationException(
                $"this call or action needs services of type {typeof(TServices).Name}, but the step was given " +
                $"{(services is null ? "none" : services.GetType().Name)}. Pass them to StatechartStep.AdvanceAsync.");

    /// <summary>
    /// A call with no output. <c>onDone</c> still fires when it finishes, with a null output. An invoke with
    /// no <c>onDone</c> at all is how a chart runs a call and stays where it is.
    /// </summary>
    public static StatechartCall Of<TContext>(Func<CallArgs<TContext>, CancellationToken, Task> run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new StatechartCall(async (call, ct) =>
        {
            await run(Typed<TContext>(call), ct).ConfigureAwait(false);
            return null;
        });
    }

    internal Task<object?> RunAsync(CallContext call, CancellationToken ct) => _run(call, ct);

    private static CallArgs<TContext> Typed<TContext>(CallContext call) =>
        new(call.Id, (TContext)call.Context!, call.Input, call.Event, $"{call.Id}:{call.Incarnation}");

    // The machine holds a call as a child actor only for as long as the step that runs it. The step completes
    // every call it starts before it persists anything, so this logic never has state of its own to keep.
    (ISnapshot State, IReadOnlyList<Effect> Effects) IActorLogic.GetInitialSnapshot(object? input) =>
        (Running.Instance, []);

    (ISnapshot State, IReadOnlyList<Effect> Effects) IActorLogic.Transition(ISnapshot state, MachineEvent evt) =>
        (state, []);

    private sealed class Running : ISnapshot
    {
        public static readonly Running Instance = new();
        public SnapshotStatus Status => SnapshotStatus.Active;
        public object? Output => null;
        public Exception? Error => null;
    }
}

/// <summary>What a call receives.</summary>
/// <param name="Id">The invoke's id: the <c>id</c> the chart gives it, or one the library generates.</param>
/// <param name="Context">The machine context at the time the call starts, including any assigns that ran
/// on the way into the invoking state.</param>
/// <param name="Input">The invoke's <c>input</c>, as the chart declares it. Null when it declares none.</param>
/// <param name="Event">The event that moved the machine into the invoking state: a raised event with its
/// payload, the completion of an earlier call, a timer, or the start event on a new instance.</param>
/// <param name="InvocationKey">
/// Identifies this invocation within the workflow instance: the invoke's id and a counter the snapshot
/// carries. A retry of the step gets the same key, because it runs again from the same snapshot. A later
/// invocation of the same invoke, for example on the next pass of a loop, gets a new key. Combine it with a
/// business key from the seed input to make an idempotency key for the system the call reaches. Give each
/// invoke an explicit <c>id</c> in the chart, so the key does not depend on a generated name.
/// </param>
public sealed record CallArgs<TContext>(string Id, TContext Context, object? Input, MachineEvent Event, string InvocationKey);

internal sealed record CallContext(
    string Id, object? Context, object? Input, MachineEvent Event, string Incarnation, object? Services);

/// <summary>
/// Thrown by a call to take its invoke's <c>onError</c> transitions. Throw it for a business outcome that the
/// chart draws as an error branch, for example "the identity provider refused this user".
/// <para>
/// Every other exception means "this attempt did not complete": the step fails and the runtime retries it.
/// Thus a timeout or a refused connection retries and does not send the flow down an error branch.
/// </para>
/// <para>
/// An <c>onError</c> guard or action reads this exception from the event:
/// <c>((ErrorActorEvent)args.Event).Error</c>.
/// </para>
/// </summary>
public sealed class StatechartCallFailedException : Exception
{
    /// <summary>Takes <c>onError</c> with a reason and, optionally, detail for its guards to read.</summary>
    public StatechartCallFailedException(string reason, object? detail = null)
        : base(reason)
    {
        Reason = reason;
        Detail = detail;
    }

    /// <summary>Why the call failed, in words a guard can compare.</summary>
    public string Reason { get; }

    /// <summary>Anything else the call wants the error branch to see.</summary>
    public object? Detail { get; }
}
