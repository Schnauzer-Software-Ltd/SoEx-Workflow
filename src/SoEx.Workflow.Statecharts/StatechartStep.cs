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

    // For each machine version: whether each invoke id has onError transitions. Read when a call fails, so a
    // business failure with no error branch is reported by name. Null for an id that two invokes share with
    // different answers.
    private readonly Dictionary<IMachineLogic, IReadOnlyDictionary<string, bool?>> _onError =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>The id of the machine this step runs, which is also the id every snapshot of it names.</summary>
    internal string MachineId => _machine.Id;

    /// <summary>
    /// Binds one machine. Enough for a chart that has never been revised under live instances — every
    /// snapshot in flight was written by this exact version.
    /// </summary>
    public StatechartStep(IMachineLogic machine, StatechartOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(machine);
        _machine = StatechartDurability.Require(machine);
        _options = options ?? new StatechartOptions();
        _onError[machine] = StatechartDurability.OnErrorByInvoke(machine);
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
        _versions = StatechartDurability.Require(versions);
        _options = options ?? new StatechartOptions();

        // Resolve the starting machine through the registry itself rather than taking a second reference to
        // it, so `current` is held to exactly the invariant every resumed snapshot is held to.
        _machine = versions.ParseSnapshot(new PersistedSnapshot
        {
            Status = "active",
            Value = "",
            Machine = new PersistedMachineIdentity(versions.MachineId, current),
        }).Machine;

        foreach (string version in versions.Versions)
        {
            if (versions.Get(version) is { } registered)
            {
                _onError[registered] = StatechartDurability.OnErrorByInvoke(registered);
            }
        }
    }
    private readonly JsonSerializerOptions _snapshotJson = SnapshotJson.DefaultOptions();

    private static readonly IReadOnlyDictionary<string, long> Empty =
        new Dictionary<string, long>(StringComparer.Ordinal);

    /// <summary>
    /// Builds the seed step for a new instance. It names the machine version that starts and carries
    /// <paramref name="input"/> as JSON. It runs nothing.
    /// <para>
    /// The machine starts in the first governed step. Its initial entry actions and calls therefore run under
    /// the step's seal, retry and idempotency, the same as all of its later work. A seed that ran them here
    /// would run them in whatever code starts the instance, where a failure has nothing to retry it.
    /// </para>
    /// <para>
    /// The input goes through JSON, and <see cref="StatechartOptions.InputConverter"/> reads it back before the
    /// machine sees it.
    /// </para>
    /// </summary>
    public MachineStep Seed(object? input = null) =>
        new(JsonSerializer.Serialize(Identity(_machine), _snapshotJson))
        {
            Start = JsonSerializer.Serialize(input, _snapshotJson),
        };

    // A snapshot that carries only which machine, and which version of it, the instance starts on. The version
    // is fixed here, at seed time, so a deploy between the seed and the first step does not change it.
    private static PersistedSnapshot Identity(IMachineLogic machine) => new()
    {
        Status = "active",
        Value = "",
        Machine = new PersistedMachineIdentity(machine.Id, machine.Version),
    };

    /// <summary>
    /// Advances the machine by one event and returns the flow's next action.
    /// <paramref name="data"/> is the payload a raise carried, and is null for a bare raise or a timer.
    /// <para>
    /// The machine runs until it waits or finishes. The calls it invokes on the way run here, and their
    /// results move it on inside this step. A retry of the step runs all of that work again.
    /// </para>
    /// <para>
    /// <paramref name="services"/> is handed to every call and action registered with a services type, for
    /// example the components the Manager received for this call. A SoEx component is resolved per call, so
    /// the Manager passes the ones it was given rather than the chart capturing any at load.
    /// </para>
    /// </summary>
    public async Task<WorkflowAction> AdvanceAsync(
        MachineStep step,
        MachineEventData? data = null,
        object? services = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(step);

        PersistedSnapshot snapshot = JsonSerializer.Deserialize<PersistedSnapshot>(step.Snapshot, _snapshotJson)
            ?? throw new InvalidOperationException("the machine step carried no readable snapshot");

        // Which machine wrote this? With a registry that is its question to answer, from the identity the
        // snapshot carries. An unregistered version throws here, which parks the instance with its key
        // retained, so re-registering that version recovers it rather than losing it.
        IMachineLogic machine = _versions is null ? _machine : _versions.ParseSnapshot(snapshot).Machine;

        if (step.Start is { } start)
        {
            return await StartAsync(new Run(machine, services), _options.InputConverter(start), cancellationToken)
                .ConfigureAwait(false);
        }

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

        var run = new Run(machine, services);
        try
        {
            if (step.EventName.Length > 0)
            {
                state = await RunAsync(run, state, [], cause: null, EventFor(step.EventName, data), cancellationToken)
                    .ConfigureAwait(false);
            }

            // Persisted by the machine that produced the state, which on a resume is the version that WROTE
            // the snapshot rather than the version new instances start at. An instance therefore finishes on
            // the chart it started on — pin-and-drain, the same policy the rest of the framework takes to a
            // changed flow — instead of being half-migrated mid-run by a build it never knew about.
            return Route(run, state, step.Deadlines);
        }
        finally
        {
            // The context's life ends with the step: the flow continues as a snapshot, quite possibly on
            // another node, so nothing here has a reader afterwards. Released even on the failure path, where
            // the instance parks and this node is done with it either way.
            Release(machine, state);
        }
    }

    // The first step of an instance: the initial transition, its effects, and everything they lead to.
    private async Task<WorkflowAction> StartAsync(Run run, object? input, CancellationToken ct)
    {
        (ISnapshot state, IReadOnlyList<Effect> effects) = run.Machine.GetInitialSnapshot(input);
        try
        {
            state = await RunAsync(run, state, effects, new InitEvent(input), first: null, ct).ConfigureAwait(false);
            return Route(run, state, Empty);
        }
        finally
        {
            Release(run.Machine, state);
        }
    }

    // What one step accumulates while the machine runs: which machine, what the Manager handed in, and which
    // timers the machine (re)started. A timer keeps its id when its state is re-entered, so the id alone cannot
    // tell a fresh timer from the one that just fired.
    private sealed class Run(IMachineLogic machine, object? services)
    {
        public IMachineLogic Machine { get; } = machine;
        public object? Services { get; } = services;
        public HashSet<string> StartedTimers { get; } = new(StringComparer.Ordinal);
    }

    // Runs the machine until it waits: one macrostep for the event the step was given, then one for each
    // event the machine's own work produces. Two kinds of work produce events here. A call's result is fed
    // back as the completion of its invoke, so the chart's onDone and onError guards decide on what the call
    // answered. A zero-delay raise is the machine talking to itself. Both loop inside this step rather than
    // becoming a durable round-trip: neither is a business event, nothing outside could deliver one, and
    // journaling them would put the machine's private bookkeeping into the flow's history.
    //
    // `effects` are the effects of a transition that has already run (the initial one, when the machine starts), and
    // `cause` is the event that produced them. `first` is the event to feed, if there is one.
    private async Task<ISnapshot> RunAsync(
        Run run,
        ISnapshot state,
        IReadOnlyList<Effect> effects,
        MachineEvent? cause,
        MachineEvent? first,
        CancellationToken ct)
    {
        var pending = new Queue<MachineEvent>();
        var calls = new Dictionary<string, SpawnEffect>(StringComparer.Ordinal);

        if (cause is not null)
        {
            await RunEffectsAsync(run, state, effects, cause, pending, calls, ct).ConfigureAwait(false);
        }

        if (first is not null)
        {
            pending.Enqueue(first);
        }

        int transitions = 0;
        while (pending.TryDequeue(out MachineEvent? evt))
        {
            if (++transitions > _options.MaxTransitionsPerStep)
            {
                throw new InvalidOperationException(
                    $"the machine took more than {_options.MaxTransitionsPerStep} transitions in one step without " +
                    "waiting. A chart whose calls or raises lead back into each other with no wait between them " +
                    $"never parks. Add a wait or a delay to the loop, or raise {nameof(StatechartOptions)}." +
                    $"{nameof(StatechartOptions.MaxTransitionsPerStep)} if the chain is long by design.");
            }

            (ISnapshot next, IReadOnlyList<Effect> produced) = run.Machine.Transition(state, evt);
            state = next;
            await RunEffectsAsync(run, state, produced, evt, pending, calls, ct).ConfigureAwait(false);
        }

        // Every call the machine spawned has started and completed, or was stopped before it started. A call
        // still here would be a child the snapshot cannot hold.
        if (calls.Count > 0)
        {
            throw new InvalidOperationException(
                $"the machine spawned the call(s) {string.Join(", ", calls.Keys)} and never started them");
        }

        return state;
    }

    // Runs the effects of one transition in order. Raises and call results go onto `pending`, behind every
    // effect of this transition, which is the order XState itself gives them. Anything the portable model
    // has no home for is refused by name rather than ignored: a machine that spawns a child machine, sends
    // to another actor or emits to a subscriber is relying on a runtime this deliberately does not have, and
    // silently dropping that would leave a flow that looks like it worked.
    private async Task RunEffectsAsync(
        Run run,
        ISnapshot state,
        IReadOnlyList<Effect> effects,
        MachineEvent cause,
        Queue<MachineEvent> pending,
        Dictionary<string, SpawnEffect> calls,
        CancellationToken ct)
    {
        foreach (Effect effect in effects)
        {
            switch (effect)
            {
                case ActionEffect action:
                    // A synchronous action has done its work when RunAction returns. An asynchronous one
                    // (StatechartAction) has handed its work back, and is awaited here, before the next effect.
                    foreach (Func<CancellationToken, Task> work in
                             StatechartAction.Collect(_options.RunAction, action, run.Services))
                    {
                        await work(ct).ConfigureAwait(false);
                    }

                    break;

                case RaiseEffect { Delay.Ticks: 0 } raise:
                    pending.Enqueue(raise.Event);
                    break;

                case RaiseEffect delayed:
                    // A DELAYED self-raise is how the machine starts a timer — an `after` transition. Starting
                    // it already put it on the snapshot's timer ledger, which is what Route reads to arm the
                    // durable timer. What this step must remember is THAT it started: a state that re-enters
                    // starts its timer again under the same id, and that timer needs a fresh deadline.
                    if (delayed.Id is { } timerId)
                    {
                        run.StartedTimers.Add(timerId);
                    }

                    break;

                case SpawnEffect { Logic: StatechartCall } spawn:
                    // Spawned now, started at the end of the macrostep. A state entered and left inside one
                    // macrostep is stopped before that, and its call never runs.
                    calls[spawn.Id] = spawn;
                    break;

                case StopChildEffect stop:
                    calls.Remove(stop.ChildId);
                    break;

                case StartEffect start when calls.Remove(start.Id, out SpawnEffect? spawn):
                    pending.Enqueue(await CallAsync(run, state, spawn, cause, ct).ConfigureAwait(false));
                    break;

                case DeadLetterEffect { Event: DoneActorEvent or ErrorActorEvent } refused:
                    // The step fed this completion itself, so a refusal is a defect, not a caller's mistake.
                    // Going on would leave the machine waiting for a result it has already been refused.
                    throw new InvalidOperationException(
                        $"the machine refused the result of a call ({refused.Reason}): {refused.Event.Type}");

                case TerminateEffect or StartEffect or CancelEffect or DeadLetterEffect:
                    // Terminate is read from the snapshot's status below; the rest are bookkeeping with no
                    // durable consequence of their own (a cancel drops the timer from the same ledger). A dead
                    // letter for an event from outside is the machine declining it; the flow waits on as before.
                    break;

                default:
                    throw new NotSupportedException(
                        $"a statechart-backed workflow cannot run a {effect.GetType().Name}: spawning child " +
                        "machines, sending to other actors and emitting to subscribers all need an actor runtime, " +
                        "and the portable flow deliberately has none. Model it as a workflow event, or as a " +
                        $"{nameof(StatechartCall)}.");
            }
        }
    }

    // Runs one call and turns its outcome into the completion event of its invoke. The context it sees is the
    // machine's context now, after the assigns that led into the invoking state.
    private async Task<MachineEvent> CallAsync(
        Run run, ISnapshot state, SpawnEffect spawn, MachineEvent cause, CancellationToken ct)
    {
        var call = (StatechartCall)spawn.Logic;
        try
        {
            object? output = await call.RunAsync(
                new CallContext(spawn.Id, ContextOf(run.Machine, state), spawn.Input, cause, spawn.Incarnation, run.Services),
                ct).ConfigureAwait(false);
            return new DoneActorEvent(spawn.Id, output, spawn.Incarnation);
        }
        catch (StatechartCallFailedException failed)
        {
            // The call reported a business failure the chart has no branch for. Left to the library, the
            // failed call would stay attached to the machine and the snapshot could not be written at all, so
            // the defect is named here instead.
            if (_onError.TryGetValue(run.Machine, out IReadOnlyDictionary<string, bool?>? byInvoke) &&
                byInvoke.TryGetValue(spawn.Id, out bool? hasOnError) && hasOnError == false)
            {
                throw new InvalidOperationException(
                    $"the call '{spawn.Id}' failed with reason '{failed.Reason}', and its invoke has no onError " +
                    "transition to take. Add onError to the invoke, or return the outcome as a value for onDone.",
                    failed);
            }

            return new ErrorActorEvent(spawn.Id, failed, spawn.Incarnation);
        }
    }

    // The machine's state after the step decides the action: done completes the flow, active parks it on
    // whatever can move it next.
    private WorkflowAction Route(Run run, ISnapshot state, IReadOnlyDictionary<string, long> carried)
    {
        IMachineLogic machine = run.Machine;
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
        IReadOnlyDictionary<string, long> deadlines = Deadlines(timers, carried, run.StartedTimers);

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

    /// <summary>
    /// The workflow event name a machine timer fires as. Prefixed so it cannot collide with a business event
    /// the chart declares, and so an operator reading a journal can tell the two apart.
    /// </summary>
    internal static string TimerEventName(string timerId) => TimerPrefix + timerId;

    private const string TimerPrefix = "xstate.timer.";

    /// <summary>
    /// The event to feed the machine for a resumed workflow event.
    /// <para>
    /// A timer is NOT a named event: the machine matches its own timers by <see cref="TimerEvent"/> and looks
    /// the id up in its ledger, so a named event spelled like a timer would simply not match — the chart would
    /// re-arm the timer and sit there, which is a silent stall rather than a failure. Only a timer name carries
    /// no payload; a raiser has nothing to say about a deadline passing.
    /// </para>
    /// </summary>
    private MachineEvent EventFor(string eventName, MachineEventData? data) =>
        eventName.StartsWith(TimerPrefix, StringComparison.Ordinal)
            ? new TimerEvent(eventName[TimerPrefix.Length..])
            : new NamedEvent(eventName, Payload(data));

    // Keep the deadline a timer already had; give a newly started one its deadline from now. A timer this step
    // started is new even when its id was carried: a re-entered state restarts its timer under the same id, and
    // keeping the old deadline would arm the restarted timer with nothing left on it. A timer that has gone
    // away since the last step drops out, so the carried set never grows stale entries.
    private IReadOnlyDictionary<string, long> Deadlines(
        PersistedTimer[] timers, IReadOnlyDictionary<string, long> carried, IReadOnlySet<string> started)
    {
        DateTimeOffset now = _options.Now();
        var next = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (PersistedTimer timer in timers)
        {
            next[timer.Id] = !started.Contains(timer.Id) && carried.TryGetValue(timer.Id, out long existing)
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
    //
    // A call that is running is a child the snapshot cannot hold, so this read tolerates one. It reads the
    // context and nothing else; nothing built here is stored.
    private static object? ContextOf(IMachineLogic machine, ISnapshot state) =>
        machine.Persist(state, ContextOnly).Context;

    private static readonly PersistOptions ContextOnly = new()
    {
        AllowInlineActors = true,
        ChildSnapshot = _ => new PersistedSnapshot { Status = "active", Value = "" },
    };

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
