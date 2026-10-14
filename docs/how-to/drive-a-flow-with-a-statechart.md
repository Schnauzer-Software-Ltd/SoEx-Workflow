> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# How to drive a flow with a statechart

You can run a statechart as a SoEx workflow. The chart can come from Stately Studio, from XState JSON, or
from SCXML. You do not translate the chart into step DTOs by hand.
Use a statechart for a flow that is easier to draw than to write.

A statechart runs as a standard **step component** behind a standard contract. It uses the portable flow.
Thus it gets all of the governed step:

- the sealed journal
- crypto-shred
- the PII guards (PII is personally identifiable information)
- idempotency
- subject enrollment

Each of these works the same as for a step component that you write by hand.

[Reference: statecharts](../reference/statecharts.md) lists each type and member that this guide uses.

## What you need

- `SoEx.Workflow.Statecharts`. This package wraps
  [statelyai/xstate-csharp](https://github.com/statelyai/xstate-csharp).
- `XState` version `0.1.0-alpha`. This package is not on nuget.org. Pack it from
  [statelyai/xstate-csharp](https://github.com/statelyai/xstate-csharp) at commit `f90200b`, and put it
  in a package source that your build can read. `SoEx.Workflow.Statecharts` references this version. The
  metadata of the package names the repository `statelyai/xstate-dotnet`. The public repository is
  `statelyai/xstate-csharp`.
- `XState.Scxml`, if you write your charts in SCXML. The adapter takes a machine as its input, so it has
  no dependency on this package.

The library reads XState **v6** JSON and SCXML. It has no runtime of its own. A transition is a pure
function from `(machine, state, event)` to `(state, effects)`. The effects are plain data. SoEx supplies
all of the durable part. The machine uses no scheduler, no timer service, and no mailbox.

**What travels.** Each node loads the chart locally at start, and the chart stays on that node. Only the
snapshot goes across the wire, on the sealed step DTO. The node feeds the snapshot back into the chart
that it holds. If the chart changed after the instance parked, the node feeds the snapshot into the
version that wrote it.

## 1. Write the Manager

1. Write the contract of the Manager with a step DTO and the data that a raise can carry.

   This contract has the same shape as the contract of each other portable Manager. It does not refer
   to the statechart. A Manager with a chart is a standard workflow Manager.

2. Implement `IErasureEvent` on the Manager.

   The composition refuses a workflow-hosted Manager without the erasure contract.

3. Take the components that the chart calls as constructor parameters of the Manager. Pass them to
   `AdvanceAsync` as `services`.

   SoEx resolves the Manager and its components for each call. Declare a proxy for each component in the
   topology, as for each other Manager. The chart holds no component. Each call and each action of the chart
   gets the components from the `services` of the step.

```csharp
public interface IExpenseManager
{
    Task<WorkflowAction> Approve(MachineStep step, MachineEventData? data = null);
}

public sealed record ExpenseComponents(IBudgetEngine Budget, INotificationAccess Notifications);

public sealed class ExpenseManager(StatechartStep process, IBudgetEngine budget, INotificationAccess notifications)
    : IExpenseManager, IErasureEvent
{
    public Task<WorkflowAction> Approve(MachineStep step, MachineEventData? data = null) =>
        process.AdvanceAsync(step, data, new ExpenseComponents(budget, notifications));

    // Mandatory for a workflow-hosted manager: the composition refuses one without the erasure contract
    // rather than letting the termination degrade to a silent no-op. A manager holding data outside the
    // sealed journal extracts it in OnRetaining, while the key is still live.
    public Task OnRetaining(RetainingContext context) => Task.CompletedTask;
    public Task OnTerminated(TerminatedContext context) => Task.CompletedTask;
    public Task OnRetentionHeld(RetentionHeldContext context) => Task.CompletedTask;
}
```

4. Keep the chart with the Manager, and load it there.

   The process belongs to the Manager. The Manager is also where you bind the names in the chart to the
   component calls behind them. [`examples/Statechart`](../../examples/Statechart/README.md) shows the
   layout.

5. Declare `MachineStep` and `MachineEventData` as known types of the message serializer, together with
   `WorkflowKnownTypes.Framework`.

```csharp
builder.SoEx(topology, new KnownTypes([
    .. WorkflowKnownTypes.Framework,
    typeof(MachineStep),
    typeof(MachineEventData),
]));
```

## 2. Choose a format, and load the chart

XState v6 JSON and SCXML both work. They keep the state of the chart in different ways:

| | JSON | SCXML |
|---|---|---|
| Chart state across a park | plain data, survives with no extra code | the datamodel is in a JavaScript engine, which the journal cannot hold |
| Conditions and assigns | supported | supported, after you name the values to carry |
| What you write | nothing | two lines that name the values to keep |

**JSON is the default.** Its context is JSON, which the snapshot carries with no extra code. You declare
no C# context type:

```csharp
StateMachine<JsonElement> machine = MachineConfig.FromJson(
    chartJson,
    new MachineImplementations<JsonElement>()
        .Action("recordApprover", args => audit.Record(args.Event))
        .Guard("isManager", args => roles.IsManager(args.Context)));
```

The context is the `context` JSON of the chart, as a `JsonElement`. Section 3 sets `ContextConverter` to
read it back after each park.

**SCXML** loads through `ScxmlConverter.Parse` in the separate `XState.Scxml` package.

1. Check the chart with `ScxmlDurability.RequireDurable` before you parse it.

   A chart that keeps values in its datamodel needs a decision.

```csharp
StateMachine<ScxmlDataModel> machine = ScxmlConverter.Parse(ScxmlDurability.RequireDurable(xml));
```

`RequireDurable` accepts a chart that routes on event names only. It refuses a chart that uses `<data>`,
`<assign>`, `cond`, `<script>`, or related elements. The refusal names what it found. It occurs before
the first instance exists, so values cannot disappear at the first wait months later.

`RequireDurable` is a default check. A chart that needs its datamodel can carry it, as section 3 shows.
That chart does not call `RequireDurable`. `DatamodelUse(xml)` gives the same answer and does not throw.
Use it to audit a folder of charts before you choose a format.

**The chart and your code are two parts.** The calls, actions, and guards of a chart are names. You supply
the code for them at import. The import refuses each name that has no implementation. It reports all of
the missing names at one time. The chart sets the shape of the flow. Your code sets what each state does.

> [!CAUTION]
> Make sure that each worker loads a byte-identical chart. A replay must also load the same chart. If two
> workers have different machines, the flow diverges.

You choose where the chart comes from when you deploy:

- An embedded resource is the safest default. It is pinned to the assembly and cannot skew.
- A file or a config store is satisfactory if it is versioned and immutable for each deploy.
- Do not load the "latest" chart from a store that can change.

Load each chart **one time, at start**. Do not load it for each step. A machine is an immutable value with
a pure transition function. Thus one machine serves each step of each workflow instance. A parse for each
step gives no benefit and uses real time. The chart never goes across the wire. Only the snapshot does.

## 3. Bind the chart to the flow

1. Create a `StatechartStep` with the machine and a `StatechartOptions`.
2. Set `ResumableEvents` to the events that a caller can raise.
3. Set `ContextConverter` to read the context back from JSON.

```csharp
var chart = new StatechartStep(machine, new StatechartOptions
{
    // The events an outside caller may raise at a parked machine, in the order they become wait branches.
    ResumableEvents = ["approve", "reject"],
    // The snapshot crosses the journal as JSON, so say how to read the context back.
    ContextConverter = StatechartOptions.JsonContext<JsonElement>(),
});
```

The constructor checks the machine. It refuses a chart that cannot run as a durable flow, and lists each
problem. See [Loop a state on a timer](#loop-a-state-on-a-timer) and the
[load-time check](../reference/statecharts.md#statechartdurability).

4. For an SCXML chart, also set `ScxmlContextIsNotCarried`, `CaptureContext`, `ApplyContext`, and
   `ReleaseContext`.

   The context of an SCXML chart is a live JavaScript engine. It is not data.

```csharp
var chart = new StatechartStep(machine, new StatechartOptions
{
    ResumableEvents = ["nudge", "close"],
    // The engine cannot travel, so it is not carried; a fresh one comes from the chart we already hold.
    ScxmlContextIsNotCarried = true,
    ContextConverter = _ => machine.GetInitialState().State.Context,
    // The VALUES can travel. Name the ones that must survive a park; anything you leave out is gone.
    CaptureContext = ctx => Json(((ScxmlDataModel)ctx!).GetValue("attempts")),
    ApplyContext = (ctx, json) => ((ScxmlDataModel)ctx!).Engine.SetValue("attempts", Read(json)),
    // A step is the whole life of that engine, so release it rather than leaving it to the collector.
    ReleaseContext = ctx => ((ScxmlDataModel)ctx!).Engine.Dispose(),
});
```

These lines are in your code. Thus a consumer that uses only JSON has no dependency on a JavaScript
engine.

You declare `ResumableEvents`. The library does not derive them from the machine. A snapshot does not
list the events that the machine accepts next. Also, the runtime journals each of these names in clear
text as its delivery key. Thus you must choose the names that a flow exposes. If more than one event can
be delivered at the same time, the declared order sets which one wins.

`ResumableEvents` contains only events that a person or a system outside the flow raises. The results of
calls and the raises that the chart makes to itself stay inside the step. Do not declare them.

Set `ContextConverter` for each machine, and for each chart from JSON. The snapshot goes across the journal
as JSON, and the context comes back as a dictionary. The converter makes it the context type of the machine
again. Without it, the first step after a park fails. For a chart from JSON, use
`StatechartOptions.JsonContext<JsonElement>()`. For a typed context, use `JsonContext<T>()`.

## 4. Start the flow

1. Call `chart.Seed` with the start input to make the first step.

   The seed runs nothing. It records the machine, its version, and the input.

2. Seal the step and start the instance:

```csharp
byte[] seed = step.SealStep(instanceId, chart.Seed(JsonSerializer.SerializeToElement(new { orderId })),
    WorkflowEnvelope.AmbientFor(step.Serializer, SubjectContext.Managed(email)));
await gateway.StartAsync(instanceId, seed);
```

The first governed step starts the machine. That step runs the entry actions and the calls of the initial
state. Thus the first work of the chart runs under the seal, the retry, and the idempotency of a governed
step.

The seed carries the input as JSON. The first step reads it back with `InputConverter`. By default, a JSON
object stays a `JsonElement`. For a chart from JSON, a `JsonElement` input replaces the `context` of the
chart. Thus the input must contain each context property that the chart reads. For a machine that you
build in C# with a typed context, set `InputConverter` to deserialize your input type.

The version of a new instance is fixed when you make the seed. A deploy between the seed and the first
step does not change it.

## 5. Move the flow

1. Raise one of the declared events:

```csharp
await gateway.RaiseEventAsync(instanceId, "approve",
    sealer.SealEventData(instanceId, new MachineEventData("\"ana\"")));
```

`sealer` is a `WorkflowSealer` for the flow. A host that holds the `GovernedStep` can call
`step.SealEventData` with the same arguments. See [Triggering](../reference/triggering.md).

Each wait branch seals its own continuation. Thus the branch that fires tells the next step which event
occurred. No other channel is necessary.

`MachineEventData.Data` is JSON. By default, a JSON primitive gets to the machine as the matching CLR
value. An object or an array gets to the machine as a `JsonElement`.

2. To give the transitions a type that they can pattern-match on, override `EventDataConverter`.

## Branch on the result of a call

A machine selects its transition before it runs the actions of that transition. Thus an action cannot
choose the next state. When the next state depends on the result of work, make the work a **call**. A call
is an XState `invoke`. Its `onDone` transitions read the result.

1. In the chart, put an `invoke` on the state that does the work. Give `src` the name of the call.

2. Give `onDone` one transition for each result. Put a guard on each transition except the last. The
   first transition whose guard passes wins.

   The last `onDone` transition must have no guard. The constructor of `StatechartStep` refuses an invoke
   whose `onDone` transitions all have guards. Without a default, a result that no guard accepts leaves the
   machine in the state with nothing to move it.

3. Give `onError` a transition for a business failure. Use it only if the chart has an error branch.

```json
"creatingUser": {
  "invoke": {
    "id": "createUser",
    "src": "createUser",
    "onDone": [
      { "guard": "refused", "target": "discarded" },
      { "guard": "noUser", "target": "unrecorded" },
      { "target": "creatingOrg", "actions": "rememberUser" }
    ],
    "onError": { "target": "failed" }
  }
}
```

4. Register the call with `StatechartCall.Of`, under the `src` name. Give the type of the `services`
   that the Manager passes as the second type argument.

5. Register each guard. A guard reads the result from the `Output` of the `DoneActorEvent`.

```csharp
new MachineImplementations<JsonElement>()
    .ActorLogic("createUser", StatechartCall.Of<JsonElement, RegistrationComponents>(async (call, components, ct) =>
        await components.Identity.CreateUserAsync(call.Context.GetProperty("email").GetString()!)))
    .Guard("refused", args => Result(args.Event) == "refused")
    .Guard("noUser", args => Result(args.Event) == "no-user");

static string? Result(MachineEvent evt) => (evt as DoneActorEvent)?.Output as string;
```

A call that needs no component uses `StatechartCall.Of<TContext>`, with no services type.

The step runs the call when the machine settles in the state. Then it gives the result to the machine,
and the machine takes the matching `onDone` transition. All of this occurs in the same step, with no
durable round trip. If the next state also invokes a call, that call runs next, in the same step.

A guard reads only the event and the context. It does no work. The library can evaluate a guard more
than one time.

The call receives a `CallArgs<TContext>`:

- `Context` is the context of the machine when the call starts.
- `Event` is the event that moved the machine into the state. After a raise, it is a `NamedEvent` with the
  payload of the raise.
- `Input` is the `input` of the invoke, as the chart declares it.
- `Id` is the `id` of the invoke.
- `InvocationKey` identifies this invocation in the instance. See
  [What one step runs, and what a retry runs again](#what-one-step-runs-and-what-a-retry-runs-again).

To run a call and stay in the state, give the invoke no `onDone`. For example, a waiting state can invoke
a call that sends a reminder.

### Report a business failure

1. Throw `StatechartCallFailedException` from the call. Give it a reason.

   The machine takes the `onError` transitions of the invoke.

2. To branch on the reason, read it in an `onError` guard:

```csharp
.Guard("blockedDomain", args =>
    ((ErrorActorEvent)args.Event).Error is StatechartCallFailedException { Reason: "blocked-domain" })
```

Any other exception means that the attempt did not complete. The step fails, and the runtime retries it.
Thus a timeout or a refused connection retries the work. It does not take the error branch.

Give each invoke whose call can throw `StatechartCallFailedException` an `onError`. If the invoke has no
`onError`, the step fails with an error that names the invoke and the reason. The runtime then retries the
step, and the call runs again.

### Keep a result for a later state

The result of a call is in memory for the step only. To use it in a later state, or after a wait, store it
in the context with an assign. A later call reads it from `CallArgs.Context`.

1. Add an action to the `onDone` transition in the chart, for example `"actions": "rememberUser"`.
2. Register the action as an `AssignAction` that returns the new context:

```csharp
.Action("rememberUser", new AssignAction<JsonElement>(args =>
    args.Context.With("userId", (args.Event as DoneActorEvent)?.Output)))
```

`With` is in `StatechartJson`. A `JsonElement` cannot change, so `With` returns a copy with the property
set. The snapshot carries the context, so the value survives each park.

> [!WARNING]
> Store only the values that later states need. The context is part of the snapshot. The snapshot is
> sealed and crypto-shred covers it. The output of a final state goes to the journal in clear text. Keep
> PII out of the `output` of each final state.

## Run asynchronous actions

Use an action for work whose result the chart does not branch on, for example a notification.

1. Register the action with `StatechartAction.Of`:

```csharp
.Action("sendWelcome", StatechartAction.Of<JsonElement, RegistrationComponents>((args, components, ct) =>
    components.Mail.SendWelcomeAsync(args.Context.GetProperty("email").GetString()!)))
```

The step awaits the action in its position among the effects of the transition. The next effect starts
after the action completes. An exception from the action fails the step, and the runtime retries the step.

A synchronous action that you register with `.Action(name, args => ...)` also works. It must not block on
asynchronous work.

## What one step runs, and what a retry runs again

A step runs the machine from the event of the step until the machine waits or finishes. On the way, it
runs each action and each call that the machine reaches. One step can thus run a chain of calls.

- **Retry.** If any call or action in the chain fails, the whole step fails. The runtime retries the step
  from the last wait, under its retry policy. Thus the retry runs the calls before the failure again. Make
  each call and each action idempotent. After the last attempt, the instance parks with its key retained.
  [Step failure, retry, and poison](../reference/runtime-matrix.md#step-failure-retry-and-poison) gives
  the policy of each runtime. On Restate, a failed step retries with no limit.
- **Idempotency key.** `CallArgs.InvocationKey` is the id of the invoke and a counter from the snapshot. A
  retry of the step gets the same key. The next invocation of the same invoke, for example on the next
  pass of a loop, gets a new key. The key is unique in one instance only. Put a business key in the seed
  input, and combine it with `InvocationKey` to make the idempotency key for the system that the call
  reaches. Give each invoke an `id` in the chart. A generated id changes when the state path changes.
- **Actions have no key.** An action that needs an idempotency key can derive one from the context.
  Alternatively, make the work a call.
- **Length.** The whole chain is one step of the runtime. Keep a chain short enough for the step limits
  of your runtime.
- **Loops.** If calls lead back into each other with no wait between them, the step stops after
  `MaxTransitionsPerStep` transitions (default 1000) and throws. Raise the limit for a long chain by
  design.
- **Cancellation.** A step operation has no `CancellationToken`. A Manager calls `AdvanceAsync` without
  one. Set a time limit on each call in its own code, for example a timeout on the client that it uses. A
  host that has a token can pass it to `AdvanceAsync`. The step gives it to each call and each
  asynchronous action. A cancellation fails the step. It does not take an error branch.
- **The machine `error` state.** The step throws, and the runtime retries the step under the same policy.

## Loop a state on a timer

A transition that targets its own state does not exit that state, by default. Thus its entry actions do
not run again, and its `after` timers do not start again. This is the XState v5 and v6 rule.

1. To make a timed loop, set `"reenter": true` on the `after` transition:

```json
"queued": {
  "entry": "release",
  "after": { "releaseWindow": { "target": "queued", "reenter": true } }
}
```

2. Register a named delay, such as `releaseWindow`, with the machine implementations:
   `.Delay("releaseWindow", TimeSpan.FromMinutes(5))`. A number of milliseconds in the chart needs no
   registration.

Each time the timer fires, the state exits and enters again. The entry action runs, and the step arms a
new durable timer.

The constructor of `StatechartStep` refuses an `after` transition that targets its own state without
`"reenter": true`. That loop runs one time and then has no timer.

An event transition that targets its own state, such as `"on": { "nudge": "open" }`, passes the check. It
keeps the timers of the state in operation. Use it for an event that must not restart a deadline.

To run actions after a delay and stay in the state, give the `after` transition actions and no target.

## Many charts, one step

You can give each chart its own contract and its own binding. Each flow then has its own flow key. Thus
each flow has its own gateway, sealer, and authorization policy. Use this design when the flows have
different governance.

If many flows have the same governance, one component can serve all of them:

```csharp
var router = new StatechartRouter(new Dictionary<string, StatechartStep>
{
    ["approval"] = new(MachineConfig.FromJson(approvalJson), approvalOptions),
    ["refund"]   = new(MachineConfig.FromJson(refundJson),   refundOptions),
});

// the one step component, with no per-chart code in it
public Task<WorkflowAction> Run(MachineStep step, MachineEventData? data = null) =>
    router.AdvanceAsync(step, data, new MembershipComponents(identity, mail));
```

Register each chart under its machine id: the `id` at the top of the chart. The router refuses a chart
whose key is different.

The routing key is the machine id in the snapshot. A chart writes its own name in each snapshot, so you
keep no separate routing table. Start an instance with `router.Seed("approval", input)`.

The router refuses a snapshot that names a chart that the host does not serve. The error message lists
the charts that the host serves. Thus, if you remove a chart from a deployment, its instances park with a
clear error. They do not go to an incorrect chart.

The router has one flow key. One binding gives one gateway and one authorization policy for all of its
charts.

## How the machine maps onto a flow

| The machine | The flow |
|---|---|
| final state (`done`) | `Complete`, with the output of the machine **as JSON** |
| `error` state | the step throws. The runtime retries the step, then parks the instance with its key retained. |
| active, with declared events | `WaitForEvent`, one branch for each declared event |
| `after(...)` delayed transition | the durable `Timeout` of the wait, which resumes into `xstate.timer.<id>` |
| `invoke` of a `StatechartCall` | the call runs in the step, and its result moves the machine in the same step |
| immediate raise | the machine continues in the same step, with no durable round trip |
| entry, exit, and transition actions | run inside the step, under its retry and idempotency |

**The result is JSON.** A chart has no CLR output type. Its output is the JSON that it declares. An
allow-listed serializer writes an untyped value in the result slot only when it gets the type by name.
Thus a flow with a statechart completes with the output of its machine serialized as JSON. The consumer
deserializes it. The same rule applies to a machine built in C#.

**Timer deadlines.** A snapshot records the *declared delay* of a timer. It does not record the deadline.
Without the deadline, an unrelated event that resumes the flow re-arms the timer from its full delay. A
caller can then delay an SLA forever with repeated events. Thus the deadline travels on the step DTO. The
durable timer arms with the time that remains. The step does this calculation off the replay path of each
runtime. The runtime journals the result delay one time, with the action of the step.

**A `StatechartStep` holds no state between steps.** It keeps only what it was composed with: the machine,
the versions, and the options. It keeps no per-instance state. Thus a node collects nothing as instances
go through it. A step releases what it builds. `ReleaseContext` does this. It is most important for
SCXML, because each step builds a JavaScript engine that has no reader after the step returns.

**A timer with no declared events.** A wait needs at least one branch. A machine that waits only on an
`after(...)` transition gets a branch with the name of the timer. The runtime journals that name in clear
text, the same as other branch names. A caller who knows the name can fire the timer early. If you want
only the timer to move the flow, declare a resumable event that the chart does not handle. The wait then
uses that event as its branch, and the timer is the timeout of the wait. A raise of that event leaves the
machine in its state, and the flow waits again with the same deadline.

**Refused effects.** These effects need an actor runtime:

- invoke or spawn a child machine
- send to a different actor
- emit to subscribers

The portable flow has no actor runtime by design. The constructor of `StatechartStep` refuses an invoke
whose source is not a `StatechartCall`. Each of the other effects throws an error that names it when the
step reaches it. Use a `StatechartCall` for work, and use workflow events for messages from outside.

## Change a chart while instances run

A snapshot describes itself: it carries the id and the version of the chart that wrote it.

1. Register each version that can still be in storage.
2. Set the version at which new instances start:

```csharp
var chart = new StatechartStep(MachineVersions.Create(v1, v2), current: "2.0.0", options);
```

An instance that parked under v1 resumes on v1 and completes on v1. This policy is pin-and-drain. The
remainder of the framework uses the same policy for a changed flow. A build that the instance did not
start on never migrates it in the middle of a run. New instances start on the current version.

3. Keep a superseded version registered until its instances drain.

If you remove a version too early, its instances fail with a clear error on restore. They park with their
keys retained. Register the version again to recover them. The framework does not lose these instances
and does not misread their state.

## Upgrade from an earlier version of this package

This version changes the API of `StatechartStep` and `StatechartRouter`:

1. Replace `Advance(step, data)` with `AdvanceAsync(step, data)`. Return its task from the step operation.
   Remove `Task.FromResult`.
2. Move work that ran in `Seed` out of the code that starts the instance. `Seed` now runs nothing. The
   first governed step runs the initial entry actions.
3. If your machine has a typed context and a non-primitive start input, set `InputConverter`.
4. Replace each action that blocks on asynchronous work with `StatechartAction.Of`.
5. Move each component that the chart captured at load to the constructor of the Manager. Pass the
   components to `AdvanceAsync` as `services`.
6. Fix each problem that the constructor reports. A chart that invokes a child machine now fails when you
   create the `StatechartStep`. It used to fail at the first step.

Instances in flight continue. A seed step from the earlier version holds the initial snapshot, and the
first step resumes it as before.

## Supported runtimes

A statechart works on each runtime that supports the portable flow, with no adapter change. It is a step
component, and the runtimes never see the machine. The behavior on each runtime is the behavior of the
portable flow on that runtime:

- **Temporal, Durable Task, InProc**: the standard behavior of the portable flow.
- **Elsa**: portable durable timers need a resumer that the consumer drives. Until you wire one, a machine
  parked on an `after(...)` transition waits with no limit. For a machine that uses many timers, we
  recommend that you use Temporal.
- **Restate**: a durable promise is write-once for each event name in each generation. A statechart flow
  does not take a `Loop`, so the whole life of the flow is one generation. Thus, on Restate, each event
  name can resume a statechart flow one time only. Use a different runtime for a chart that the same event
  can resume more than one time, for example a resend button.
- **Camunda 8 / Zeebe**: supports native BPMN only and has no portable flow. Thus a step component with a
  machine is not available. The BPMN graph is the flow on that runtime.

[The runtime matrix](../reference/runtime-matrix.md) gives the full comparison.

## A runnable example

[`examples/Statechart`](../../examples/Statechart/README.md) shows all of this from start to end. It needs
no runtime server. It contains:

- an embedded chart
- a call to an Engine whose result chooses the next state
- asynchronous notification actions
- a raise that carries data
- a timer of the chart that escalates a flow
- the crypto-shred at termination

Run it with `dotnet run --project examples/Statechart`.

## See also

- [Reference: statecharts](../reference/statecharts.md) lists each type, member, and default.
- [Write a step component](write-a-step-component.md) describes the contract shape that a statechart uses.
- [`WorkflowAction`](../reference/workflow-action.md) describes what the mapping produces, with event data.
