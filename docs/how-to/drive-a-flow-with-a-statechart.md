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

## What you need

- `SoEx.Workflow.Statecharts`. This package wraps
  [statelyai/xstate-csharp](https://github.com/statelyai/xstate-csharp).
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

```csharp
public interface IExpenseManager
{
    Task<WorkflowAction> Approve(MachineStep step, MachineEventData? data = null);
}

public sealed class ExpenseManager(StatechartStep process) : IExpenseManager, IErasureEvent
{
    public Task<WorkflowAction> Approve(MachineStep step, MachineEventData? data = null) =>
        Task.FromResult(process.Advance(step, data));

    // Mandatory for a workflow-hosted manager: the composition refuses one without the erasure contract
    // rather than letting the termination degrade to a silent no-op. A manager holding data outside the
    // sealed journal extracts it in OnRetaining, while the key is still live.
    public Task OnRetaining(RetainingContext context) => Task.CompletedTask;
    public Task OnTerminated(TerminatedContext context) => Task.CompletedTask;
    public Task OnRetentionHeld(RetentionHeldContext context) => Task.CompletedTask;
}
```

3. Keep the chart with the Manager, and load it there.

   The process belongs to the Manager. The Manager is also where you bind the named actions of the chart
   to the component calls behind them. [`examples/Statechart`](../../examples/Statechart/README.md) shows
   the layout.

## 2. Choose a format, and load the chart

XState v6 JSON and SCXML both work. They keep the state of the chart in different ways:

| | JSON | SCXML |
|---|---|---|
| Chart state across a park | plain data, survives with no extra code | the datamodel is in a JavaScript engine, which the journal cannot hold |
| Conditions and assigns | supported | supported, after you name the values to carry |
| What you write | nothing | two lines that name the values to keep |

**JSON is the default.** Its context is plain data. Thus you declare nothing:

```csharp
StateMachine<JsonElement> machine = MachineConfig.FromJson(
    chartJson,
    new MachineImplementations<JsonElement>()
        .Action("recordApprover", args => audit.Record(args.Event))
        .Guard("isManager", args => roles.IsManager(args.Context)));
```

The context is the `context` JSON of the chart. You declare no C# context type.

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

**The chart and your code are two parts.** The actions and guards of a chart are names. You supply the
code for them at import. The import refuses each name that has no implementation. It reports all of the
missing names at one time. The chart sets the shape of the flow. Your code sets what each state does.

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

You need `ContextConverter` when the context of the machine is not plain data. The snapshot goes across
the journal as JSON. Without a converter, an object slot comes back as a dictionary.

## 4. Start the flow

1. Call `chart.Seed` to make the first step.

   The first step is the initial snapshot of the machine, after its entry actions run.

2. Seal the step and start the instance:

```csharp
byte[] seed = step.SealStep(instanceId, chart.Seed("order-42"),
    WorkflowEnvelope.AmbientFor(step.Serializer, SubjectContext.Managed(email)));
await gateway.StartAsync(instanceId, seed);
```

## 5. Move the flow

1. Raise one of the declared events:

```csharp
await gateway.RaiseEventAsync(instanceId, "approve",
    sealer.SealEventData(instanceId, new MachineEventData("\"ana\"")));
```

Each wait branch seals its own continuation. Thus the branch that fires tells the next step which event
occurred. No other channel is necessary.

`MachineEventData.Data` is JSON. By default, a JSON primitive gets to the machine as the matching CLR
value. An object or an array gets to the machine as a `JsonElement`.

2. To give the transitions a type that they can pattern-match on, override `EventDataConverter`.

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
    Task.FromResult(router.Advance(step, data));
```

The routing key is the machine id in the snapshot. A chart writes its own name in each snapshot, so you
keep no separate routing table. Start an instance with `router.Seed("approval")`.

The router refuses a snapshot that names a chart that the host does not serve. The error message lists
the charts that the host serves. Thus, if you remove a chart from a deployment, its instances park with a
clear error. They do not go to an incorrect chart.

The router has one flow key. One binding gives one gateway and one authorization policy for all of its
charts.

## How the machine maps onto a flow

| The machine | The flow |
|---|---|
| final state (`done`) | `Complete`, with the output of the machine **as JSON** |
| `error` state | the step throws, so the instance parks with its key retained |
| active, with declared events | `WaitForEvent`, one branch for each declared event |
| `after(...)` delayed transition | the durable `Timeout` of the wait, which resumes into `xstate.timer.<id>` |
| zero-delay self-raise | loops inside the same step, with no durable round trip |
| entry/exit actions | run inside the step, under its retry and idempotency |

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
only the timer to move the flow, declare a resumable event.

**Refused effects.** These effects need an actor runtime:

- spawn a child
- send to a different actor
- emit to subscribers

The portable flow has no actor runtime by design. Each of these effects throws an error that names it.
An effect that disappears with no error leaves a flow that seems to work. Use workflow events for these
effects.

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

## Supported runtimes

A statechart works on each runtime that supports the portable flow, with no adapter change. It is a step
component, and the runtimes never see the machine. The behavior on each runtime is the behavior of the
portable flow on that runtime:

- **Temporal, Durable Task, InProc**: the standard behavior of the portable flow.
- **Elsa**: portable durable timers need a resumer that the consumer drives. Until you wire one, a machine
  parked on an `after(...)` transition waits with no limit. For a machine that uses many timers, we
  recommend that you use Temporal.
- **Restate**: a durable promise is write-once for each event name in each generation. Restate cannot run
  a machine that the *same* event name can resume two times in one generation.
- **Camunda 8 / Zeebe**: supports native BPMN only and has no portable flow. Thus a step component with a
  machine is not available. The BPMN graph is the flow on that runtime.

[The runtime matrix](../reference/runtime-matrix.md) gives the full comparison.

## A runnable example

[`examples/Statechart`](../../examples/Statechart/README.md) shows all of this from start to end. It needs
no runtime server. It contains:

- an embedded chart
- a raise that carries data
- a timer of the chart that escalates a flow
- the crypto-shred at termination

Run it with `dotnet run --project examples/Statechart`.

## See also

- [Write a step component](write-a-step-component.md) describes the contract shape that a statechart uses.
- [`WorkflowAction`](../reference/workflow-action.md) describes what the mapping produces, with event data.
