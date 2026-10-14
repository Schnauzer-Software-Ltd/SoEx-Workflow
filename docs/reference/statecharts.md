> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Reference: statecharts

`SoEx.Workflow.Statecharts` runs an XState machine as a step component of the portable flow. This page
lists its types, their members, and how the machine maps onto a flow. Namespace:
`SoEx.Workflow.Statecharts`. For the procedure, see
[Drive a flow with a statechart](../how-to/drive-a-flow-with-a-statechart.md).

## `StatechartStep`

`StatechartStep` runs one step of a machine-backed flow. It holds the machine, the versions, and the
options. It holds no per-instance state. Create it one time, at start, and share it.

| Member | Behavior |
|---|---|
| `StatechartStep(IMachineLogic machine, StatechartOptions? options = null)` | Binds one machine. Runs [the load-time check](#statechartdurability) and throws if the machine fails it. |
| `StatechartStep(MachineVersions versions, string current, StatechartOptions? options = null)` | Binds a registry of the versions of one chart. New instances start on `current`. Runs the load-time check on each version. |
| `MachineStep Seed(object? input = null)` | Makes the seed step for a new instance. It runs nothing. It records the machine id, the version of the machine, and `input` as JSON. |
| `Task<WorkflowAction> AdvanceAsync(MachineStep step, MachineEventData? data = null, object? services = null, CancellationToken cancellationToken = default)` | Runs one step. On a seed step, it starts the machine. On a later step, it restores the snapshot and gives the machine the event of the step. Then it runs the machine until the machine waits or finishes, and returns one `WorkflowAction`. `services` goes to each call and action that you register with a services type. |

A step runs these items, in this order, until the machine waits or finishes:

1. The transition for the event of the step. On a seed step, this is the initial transition.
2. The actions of that transition, in order. The step awaits each asynchronous action before it starts
   the next effect.
3. Each call that the transition starts. The result of the call goes back to the machine as a new event.
4. The transition for each event from item 3, and for each immediate raise. Each of these transitions
   starts again at item 2.

All of this work is one governed step. If the step fails, the runtime retries the whole step from the
last wait. Thus each call and each action must be idempotent.

Pass the components that the Manager received for the call as `services`. A SoEx component is resolved for
each call, so the chart holds no component.

## `StatechartRouter`

`StatechartRouter` is one step component for many charts. It sends each step to the chart that wrote its
snapshot.

| Member | Behavior |
|---|---|
| `StatechartRouter(IReadOnlyDictionary<string, StatechartStep> charts)` | Registers each chart. The key of each chart must be its machine id, the `id` at the top of the chart. Throws `ArgumentException` for a different key. |
| `MachineStep Seed(string machineId, object? input = null)` | Makes the seed step of the named chart. |
| `Task<WorkflowAction> AdvanceAsync(MachineStep step, MachineEventData? data = null, object? services = null, CancellationToken cancellationToken = default)` | Runs the step on the chart that the snapshot names. Throws if no chart of that name is registered. |
| `string MachineIdOf(MachineStep step)` | The machine id that the snapshot of the step names. |

## `StatechartOptions`

| Property | Default | Meaning |
|---|---|---|
| `ResumableEvents` | empty | The event names that a caller can raise at a parked machine. Each name becomes a wait branch, in this order. The journal keeps each name in clear text. |
| `ContextConverter` | null | Reads the context back after the snapshot crosses the journal. Required for each machine: without it, the first step after a park fails. Use `StatechartOptions.JsonContext<T>()`. A chart from JSON uses `JsonContext<JsonElement>()`. |
| `InputConverter` | primitives unwrap | Reads the JSON of the seed input back before the machine starts. A JSON string, number, bool, or null becomes the CLR value. An object or an array stays a `JsonElement`. |
| `EventDataConverter` | primitives unwrap | Reads the JSON of `MachineEventData.Data` into the payload of the event. Same default as `InputConverter`. |
| `RunAction` | `effect => effect.Exec()` | Runs each `ActionEffect`. Replace it to wrap the call, for example for logging. A replacement must call `Exec` before it returns. |
| `MaxTransitionsPerStep` | 1000 | The maximum number of transitions in one step. If a machine takes more transitions without a wait, the step throws. |
| `Now` | `DateTimeOffset.UtcNow` | The clock for timer deadlines. |
| `CaptureContext`, `ApplyContext`, `ReleaseContext`, `ScxmlContextIsNotCarried` | null / false | For SCXML charts. See the how-to guide. |

## `StatechartCall`

`StatechartCall` is the code behind an `invoke`. Register it as an actor source, with the name that the
`src` of the invoke uses:

```csharp
new MachineImplementations<JsonElement>()
    .ActorLogic("createUser", StatechartCall.Of<JsonElement, RegistrationComponents>(async (call, components, ct) =>
        await components.Identity.CreateUserAsync(call.Context.GetProperty("email").GetString()!)));
```

| Member | Behavior |
|---|---|
| `static StatechartCall Of<TContext>(Func<CallArgs<TContext>, CancellationToken, Task<object?>> run)` | A call that returns an output. |
| `static StatechartCall Of<TContext>(Func<CallArgs<TContext>, CancellationToken, Task> run)` | A call with no output. Its `onDone` receives a null output. |
| `static StatechartCall Of<TContext, TServices>(Func<CallArgs<TContext>, TServices, CancellationToken, Task<object?>> run)` | A call that returns an output and gets the `services` of the step. |
| `static StatechartCall Of<TContext, TServices>(Func<CallArgs<TContext>, TServices, CancellationToken, Task> run)` | A call with no output that gets the `services` of the step. |

If the step has no `services` of type `TServices`, a call with a services type throws
`InvalidOperationException`. The message names the type.

The step starts a call when the machine settles in the state that invokes it. A state that the machine
enters and leaves in the same macrostep does not start its call. The outcome of the call decides the
next event:

| The call | The event | The chart takes |
|---|---|---|
| returns a value | `DoneActorEvent`, with the value in `Output` | the `onDone` transitions of the invoke |
| throws `StatechartCallFailedException` | `ErrorActorEvent`, with the exception in `Error` | the `onError` transitions of the invoke. If the invoke has no `onError`, the step fails with an error that names the invoke and the reason. |
| throws a different exception | none | nothing. The step fails, and the runtime retries the step. |

A guard reads the output from the event:

```csharp
.Guard("refused", args => (args.Event as DoneActorEvent)?.Output as string == "refused")
```

An invoke with no `onDone` runs its call, and the machine stays in its state. If an invoke has `onDone`
transitions, the last one must have no guard.

The output of a call stays in memory for the step. To keep it after the step, store it in the context
with an assign. See [`StatechartJson`](#statechartjson).

## `CallArgs<TContext>`

| Property | Meaning |
|---|---|
| `Id` | The `id` of the invoke. If the chart gives no `id`, the library makes one. |
| `Context` | The context of the machine when the call starts. It includes the assigns of the transition into the state. |
| `Input` | The `input` of the invoke, as the chart declares it. Null if the chart declares none. |
| `Event` | The event that moved the machine into the state: a `NamedEvent` with the payload of a raise, the `DoneActorEvent` of an earlier call, a `TimerEvent`, or the `InitEvent` of a new instance. |
| `InvocationKey` | The `id` of the invoke and a counter from the snapshot, for example `send:2`. A retry of the step gets the same key. The next invocation of the same invoke gets a new key. The key is unique in one instance. Combine it with a business key from the seed input to make an idempotency key. |

## `StatechartCallFailedException`

Throw it from a call to take the `onError` transitions of the invoke.

| Member | Meaning |
|---|---|
| `StatechartCallFailedException(string reason, object? detail = null)` | Makes the exception. |
| `Reason` | The reason, for a guard to compare. |
| `Detail` | Other data for the error branch. |

An `onError` guard or action reads it with `((ErrorActorEvent)args.Event).Error`.

## `StatechartAction`

`StatechartAction` makes a named action that awaits. Register it with the name that the chart uses:

```csharp
.Action("sendWelcome", StatechartAction.Of<JsonElement, RegistrationComponents>((args, components, ct) =>
    components.Mail.SendWelcomeAsync(args.Context.GetProperty("email").GetString()!)))
```

| Member | Behavior |
|---|---|
| `static Action<ActionArgs<TContext>> Of<TContext>(Func<ActionArgs<TContext>, CancellationToken, Task> run)` | An action that the step awaits in its position among the effects of the transition. |
| `static Action<ActionArgs<TContext>> Of<TContext, TServices>(Func<ActionArgs<TContext>, TServices, CancellationToken, Task> run)` | The same, and it gets the `services` of the step. |

An action cannot change the transition that the machine takes. If the next state depends on the result of
the work, use a `StatechartCall`. An asynchronous action runs only inside a `StatechartStep`. If other code
runs it, it throws `InvalidOperationException`.

## `StatechartJson`

| Member | Behavior |
|---|---|
| `static JsonElement With(this JsonElement context, string name, object? value)` | Returns a copy of the context with the property `name` set to `value`. The context must be a JSON object. |

A `JsonElement` cannot change, so an assign returns a new context:

```csharp
.Action("rememberUser", new AssignAction<JsonElement>(args =>
    args.Context.With("userId", (args.Event as DoneActorEvent)?.Output)))
```

## `StatechartDurability`

`StatechartDurability` checks a machine for constructs that cannot run as a durable flow. Each
`StatechartStep` constructor runs this check.

| Member | Behavior |
|---|---|
| `static IReadOnlyList<string> Problems(IMachineLogic machine)` | One line for each problem. Empty if the machine passes. |
| `static T Require<T>(T machine) where T : IMachineLogic` | Returns the machine if it passes. Throws `InvalidOperationException` with all of the problems if it fails. |
| `static MachineVersions Require(MachineVersions versions)` | Checks each version in the registry. |

The check refuses:

- An `after` transition that targets its own state and does not set `"reenter": true`. That transition
  does not run the entry actions again and does not start the timers again. Thus the loop runs one time.
- An `invoke` whose source is not a `StatechartCall`.
- An `invoke` with a `timeout`, an `onTimeout`, or an `onSnapshot`.
- An `invoke` whose `onDone` transitions all have guards. A result that no guard accepts would leave the
  machine in the state with nothing to move it.

An event transition (`on`) that targets its own state passes the check. It keeps the timers of the state
in operation. Use it for an event that must not restart a deadline.

## `ScxmlDurability`

`ScxmlDurability.RequireDurable(string xml)` checks an SCXML document before you parse it.
`DatamodelUse(string xml)` gives the same answer and does not throw. See the how-to guide.

## `MachineStep` and `MachineEventData`

`MachineStep` is the step DTO of a machine-backed flow. `MachineEventData` is the data that a raise
carries. Declare both as known types of the message serializer.

| `MachineStep` member | Meaning |
|---|---|
| `Snapshot` | The snapshot of the machine, as JSON. On a seed step, it holds only the machine id and the version. |
| `EventName` | The event that this step gives the machine. Empty on a seed step. |
| `Deadlines` | The deadline of each pending timer, as UTC ticks by timer id. |
| `ContextData` | Context values that the snapshot cannot hold, as JSON. For SCXML. |
| `Start` | On a seed step, the start input as JSON. Null on each later step. |

| `MachineEventData` member | Meaning |
|---|---|
| `Data` | The payload of the event, as JSON. |

## How the machine maps onto a flow

| The machine | The flow |
|---|---|
| final state (`done`) | `Complete`, with the output of the machine as JSON |
| `error` state | the step throws. The runtime retries the step under its retry policy. After the last attempt, the instance parks with its key retained. |
| active, with declared events | `WaitForEvent`, one branch for each declared event |
| `after(...)` delayed transition | the durable `Timeout` of the wait, which resumes into `xstate.timer.<id>` |
| `invoke` of a `StatechartCall` | the call runs in the step, and its result moves the machine in the same step |
| immediate raise | the machine continues in the same step |
| entry, exit, and transition actions | run in the step, under its retry and idempotency |
| invoke of a child machine, send to a different actor, emit to subscribers | refused. The step throws an error that names the effect. |
