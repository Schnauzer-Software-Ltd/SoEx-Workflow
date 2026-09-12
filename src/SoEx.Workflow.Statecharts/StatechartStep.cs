using System.Text.Json;
using XState;
using XState.Persistence;

namespace SoEx.Workflow.Statecharts;

/// <summary>
/// Runs one step of a statechart-backed workflow: restore the machine from the step's snapshot, feed it one
/// event, run whatever that transition produced, and translate the result into a single
/// <see cref="WorkflowAction"/>.
/// <para>
/// This is a step component, not a new flow model. It sits behind an ordinary consumer contract
/// (<c>Task&lt;WorkflowAction&gt; Run(MachineStep step, MachineEventData? data = null)</c>), so it inherits
/// the whole governed-step seam unchanged — crypto-shred, the PII guards, idempotency, subject enrollment —
/// and runs on every portable runtime without a driver, wire or sidecar change. The machine decides what
/// happens next; the framework decides how that survives a restart.
/// </para>
/// <para>
/// The library ships no runtime of its own: a transition is a pure <c>(machine, state, event)</c> to
/// <c>(state, effects)</c> function and the effects are plain data. That is what makes this possible — the
/// durable part is entirely ours, and the machine never needs a scheduler, a timer service or a mailbox.
/// </para>
/// </summary>
public sealed class StatechartStep
{
    private readonly IMachineLogic _machine;
    private readonly MachineVersions? _versions;
    private readonly StatechartOptions _options;

    /// <summary>
    /// Binds one machine. Enough for a chart that has never been revised under live instances — every
    /// snapshot in flight was written by this exact version.
    /// </summary>
    public StatechartStep(IMachineLogic machine, StatechartOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(machine);
        _machine = machine;
        _options = options ?? new StatechartOptions();
    }

    /// <summary>
    /// Binds a registry of the versions of one chart that may still appear in storage, plus the version new
    /// instances start at.
    /// <para>
    /// This is the shape to use once a chart has been revised. A snapshot names the machine version that
    /// wrote it, and an instance can sit parked for months, so the build that resumes it has to be able to
    /// find that version — the registry is where it looks. Keep a superseded version registered until the
    /// instances running it have drained: drop it and those instances fail loudly on restore rather than
    /// being misread, parking with their keys retained, so re-registering the version recovers them.
    /// </para>
    /// <para>
    /// Both shapes resolve everything at composition rather than per step. A machine is an immutable value
    /// with a pure transition function, so one instance is shared safely across every step and every workflow
    /// instance; parsing a chart on each step would buy nothing and cost real time — SCXML needs a JavaScript
    /// interpreter to parse at all.
    /// </para>
    /// </summary>
    public StatechartStep(MachineVersions versions, string current, StatechartOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentException.ThrowIfNullOrEmpty(current);
        _versions = versions;
        _options = options ?? new StatechartOptions();

        // Resolve the starting machine through the registry itself rather than taking a second reference to
        // it, so `current` is held to exactly the invariant every resumed snapshot is held to.
        _machine = versions.ParseSnapshot(new PersistedSnapshot
        {
            Status = "active",
            Value = "",
            Machine = new PersistedMachineIdentity(versions.MachineId, current),
        }).Machine;
    }
    private readonly JsonSerializerOptions _snapshotJson = SnapshotJson.DefaultOptions();

    private static readonly IReadOnlyDictionary<string, long> Empty =
        new Dictionary<string, long>(StringComparer.Ordinal);

    /// <summary>Seals the seed step for a new instance: the machine's initial snapshot, before any event.</summary>
    public MachineStep Seed(object? input = null)
    {
        (ISnapshot state, IReadOnlyList<Effect> effects) = _machine.GetInitialSnapshot(input);

        try
        {
            // The initial transition can already have produced effects (entry actions, an immediately-done
            // machine). Running them here rather than on the first step keeps the seed honest: what is sealed
            // is the machine AFTER its entry work, which is what the first dispatch would otherwise have to redo.
            RunEffects(effects);
            return Persist(_machine, state, Empty);
        }
        finally
        {
            Release(_machine, state);
        }
    }

    /// <summary>
    /// Advances the machine by one event and returns the flow's next action.
    /// <paramref name="data"/> is the payload a raise carried, and is null for a bare raise or a timer.
    /// </summary>
    public WorkflowAction Advance(MachineStep step, MachineEventData? data = null)
    {
        ArgumentNullException.ThrowIfNull(step);

        PersistedSnapshot snapshot = JsonSerializer.Deserialize<PersistedSnapshot>(step.Snapshot, _snapshotJson)
            ?? throw new InvalidOperationException("the machine step carried no readable snapshot");

        // Which machine wrote this? With a registry that is its question to answer, from the identity the
        // snapshot carries. An unregistered version throws here, which parks the instance with its key
        // retained, so re-registering that version recovers it rather than losing it.
        IMachineLogic machine = _versions is null ? _machine : _versions.ParseSnapshot(snapshot).Machine;

        IRestoreResult restored = machine.Restore(snapshot, contextConverter: _options.ContextConverter);
        ISnapshot state = restored.State;

        // Put back whatever could not travel inside the snapshot. The context here is the fresh one the
        // converter supplied, so this is the point where a re-created engine gets the values it had when the
        // flow parked — before the machine sees the event and any guard reads them.
        if (step.ContextData is { Length: > 0 } captured)
        {
            (_options.ApplyContext ?? throw new InvalidOperationException(
                "the step carries captured context but the binding declares no ApplyContext to put it back — " +
                "CaptureContext and ApplyContext are two halves of one decision and must be supplied together"))
                (ContextOf(machine, state), captured);
        }

        try
        {
            if (step.EventName.Length > 0)
            {
                state = Step(machine, state, new NamedEvent(step.EventName, Payload(data)));
            }

            // Persisted by the machine that produced the state, which on a resume is the version that WROTE
            // the snapshot rather than the version new instances start at. An instance therefore finishes on
            // the chart it started on — pin-and-drain, the same policy the rest of the framework takes to a
            // changed flow — instead of being half-migrated mid-run by a build it never knew about.
            return Route(machine, state, step.Deadlines);
        }
        finally
        {
            // The context's life ends with the step: the flow continues as a snapshot, quite possibly on
            // another node, so nothing here has a reader afterwards. Released even on the failure path, where
            // the instance parks and this node is done with it either way.
            Release(machine, state);
        }
    }

    // One macrostep, plus any internal raises it produced. A RaiseEffect with no delay is the machine
    // talking to itself, so it is looped here rather than turned into a durable round-trip: it is not a
    // business event, nothing outside could deliver it, and journaling one would put the machine's private
    // bookkeeping into the flow's history.
    private ISnapshot Step(IMachineLogic machine, ISnapshot state, MachineEvent evt)
    {
        var pending = new Queue<MachineEvent>();
        pending.Enqueue(evt);

        while (pending.Count > 0)
        {
            (ISnapshot next, IReadOnlyList<Effect> effects) = machine.Transition(state, pending.Dequeue());
            state = next;

            foreach (MachineEvent raised in RunEffects(effects))
            {
                pending.Enqueue(raised);
            }
        }

        return state;
    }

    // Runs the effects of one transition and returns the internal raises to feed back in. Anything the
    // portable model has no home for is refused by name rather than ignored: a machine that spawns a child,
    // sends to another actor or emits to a subscriber is relying on a runtime this deliberately does not
    // have, and silently dropping that would leave a flow that looks like it worked.
    private List<MachineEvent> RunEffects(IReadOnlyList<Effect> effects)
    {
        List<MachineEvent> raised = [];

        foreach (Effect effect in effects)
        {
            switch (effect)
            {
                case ActionEffect action:
                    _options.RunAction(action);
                    break;

                case RaiseEffect { Delay.Ticks: 0 } raise:
                    raised.Add(raise.Event);
                    break;

                case RaiseEffect:
                    // A DELAYED self-raise is how the machine starts a timer — an `after` transition. It
                    // needs no work here: starting it already put it on the snapshot's timer ledger, which
                    // is what Route reads to arm the durable timer. Running it as an effect would be the
                    // mistake, because the delay belongs to the engine, not to this step.
                    break;

                case TerminateEffect or StartEffect or CancelEffect or DeadLetterEffect:
                    // Terminate is read from the snapshot's status below; the rest are bookkeeping with no
                    // durable consequence of their own (a cancel drops the timer from the same ledger).
                    break;

                default:
                    throw new NotSupportedException(
                        $"a statechart-backed workflow cannot run a {effect.GetType().Name}: spawning children, " +
                        "sending to other actors and emitting to subscribers all need an actor runtime, and the " +
                        "portable flow deliberately has none. Model it as a workflow event instead.");
            }
        }

        return raised;
    }

    // The machine's state after the step decides the action: done completes the flow, active parks it on
    // whatever can move it next.
    private WorkflowAction Route(IMachineLogic machine, ISnapshot state, IReadOnlyDictionary<string, long> carried)
    {
        PersistedSnapshot persisted = machine.Persist(state);

        switch (persisted.Status)
        {
            case "done":
                // The output is handed back as JSON, for the same reason the snapshot is. A machine imported
                // from a chart has no CLR output type — its output is whatever JSON the chart declared — and
                // an untyped value in the action's result slot is exactly what an allow-listed serializer
                // refuses to write without being told the type by name. JSON is one shape that every host
                // pipeline can journal, and the result is journaled in clear either way, so a consumer
                // deserializes it rather than registering a type it may not even own.
                return new WorkflowAction.Complete(Output(persisted.Output));

            case "error":
                throw new InvalidOperationException(
                    $"the machine entered its error state: {persisted.Error?.Message ?? "no detail"}");

            case "stopped":
                return new WorkflowAction.Complete(null);
        }

        // The timer ledger comes off the persisted snapshot rather than the live state: it is the same
        // ledger, already in plain-data form, and it is the form that survives into the next step.
        PersistedTimer[] timers = [.. persisted.Timers.Values];

        // Captured once for the whole wait: every branch resumes from the same parked context, so reading it
        // out per branch would do identical work N times.
        string? captured = Capture(machine, state);
        IReadOnlyDictionary<string, long> deadlines = Deadlines(timers, carried);

        List<EventBranch> branches =
        [
            .. _options.ResumableEvents.Select(name =>
                new EventBranch(name, Persist(PersistedJson(persisted), name, deadlines, captured))),
        ];

        // Nothing can move the machine on: no event it accepts from outside and no timer of its own. That is
        // a flow that would park forever, so say so here rather than letting the instance sit.
        if (branches.Count == 0 && timers.Length == 0)
        {
            throw new InvalidOperationException(
                "the machine is still active but nothing can advance it: it has no pending timer and the binding " +
                $"declares no resumable events (see {nameof(StatechartOptions)}.{nameof(StatechartOptions.ResumableEvents)})");
        }

        if (Soonest(timers, deadlines) is not ({ } due, { } timerId))
        {
            return new WorkflowAction.WaitForEvent(branches);
        }

        // The timer is armed with what is LEFT of it, not its declared delay: the machine may have been
        // parked and resumed by an event since it started, and restarting a 72-hour timer on every such
        // resume would mean it never fires. Clamped at zero for a deadline already past.
        //
        // A wait needs at least one branch, so a machine with a timer but NO declared resumable events gets
        // one named after the timer itself. That name is journaled in clear like any other, which means a
        // caller who knows it can fire the timer early — deliberate, and documented, rather than a hole:
        // declare a resumable event if you would rather the timer were the only thing that can fire.
        TimeSpan remaining = due - _options.Now();
        return new WorkflowAction.WaitForEvent(
            branches.Count > 0 ? branches : [new EventBranch(TimerEventName(timerId), Persist(PersistedJson(persisted), TimerEventName(timerId), deadlines, captured))],
            remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero,
            OnTimeout: Persist(PersistedJson(persisted), TimerEventName(timerId), deadlines, captured));
    }

    // A done machine's output as JSON, or null when it produced none. Null stays null rather than becoming
    // the four characters "null", so a flow that completed with nothing reads as nothing.
    private string? Output(object? output) =>
        output is null ? null : JsonSerializer.Serialize(output, _snapshotJson);

    /// <summary>The workflow event name a machine timer fires as — the machine sees it as its own timer id.</summary>
    internal static string TimerEventName(string timerId) => $"xstate.timer.{timerId}";

    // Keep the deadline a timer already had; give a newly started one its deadline from now. A timer that
    // has gone away since the last step drops out, so the carried set never grows stale entries.
    private IReadOnlyDictionary<string, long> Deadlines(
        PersistedTimer[] timers, IReadOnlyDictionary<string, long> carried)
    {
        DateTimeOffset now = _options.Now();
        var next = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (PersistedTimer timer in timers)
        {
            next[timer.Id] = carried.TryGetValue(timer.Id, out long existing)
                ? existing
                : now.AddMilliseconds(timer.DelayMs).UtcTicks;
        }

        return next;
    }

    private static (DateTimeOffset Due, string TimerId)? Soonest(
        PersistedTimer[] timers, IReadOnlyDictionary<string, long> deadlines)
    {
        (DateTimeOffset Due, string TimerId)? soonest = null;
        foreach (PersistedTimer timer in timers)
        {
            if (!deadlines.TryGetValue(timer.Id, out long ticks))
            {
                continue;
            }

            var due = new DateTimeOffset(ticks, TimeSpan.Zero);
            if (soonest is null || due < soonest.Value.Due)
            {
                soonest = (due, timer.Id);
            }
        }

        return soonest;
    }

    private object? Payload(MachineEventData? data) =>
        data?.Data is { Length: > 0 } json ? _options.EventDataConverter(json) : null;

    private string PersistedJson(PersistedSnapshot persisted) => JsonSerializer.Serialize(
        _options.ScxmlContextIsNotCarried ? persisted with { Context = null } : persisted, _snapshotJson);

    // The LIVE context behind a state, for the capture/apply hooks. Persist puts the context object into the
    // record as-is rather than serializing it, so this hands back the real thing — for SCXML, the datamodel
    // wrapping the engine. Only walked when a hook is supplied, so the ordinary path pays nothing.
    private static object? ContextOf(IMachineLogic machine, ISnapshot state) => machine.Persist(state).Context;

    private MachineStep Persist(IMachineLogic machine, ISnapshot state, IReadOnlyDictionary<string, long> deadlines) =>
        new(PersistedJson(machine.Persist(state)), "", deadlines, Capture(machine, state));

    private void Release(IMachineLogic machine, ISnapshot state)
    {
        if (_options.ReleaseContext is { } release)
        {
            release(ContextOf(machine, state));
        }
    }

    // Whatever of the context has to survive a park but cannot travel inside the snapshot.
    private string? Capture(IMachineLogic machine, ISnapshot state) =>
        _options.CaptureContext is { } capture ? capture(ContextOf(machine, state)) : null;

    private static MachineStep Persist(
        string snapshot, string eventName, IReadOnlyDictionary<string, long> deadlines, string? contextData) =>
        new(snapshot, eventName, deadlines, contextData);
}
