# How-to — Drive a flow with a statechart

Some flows are easier to draw than to write. If you already model a process as a statechart — in Stately
Studio, as XState JSON, or as SCXML — you can run that machine as a SoEx workflow instead of translating it
into step DTOs by hand.

This is not a third consumption model. A statechart runs as an ordinary **step component** behind an ordinary
contract, so it inherits the whole governed-step seam unchanged: the sealed journal, crypto-shred, the PII
guards, idempotency and subject enrollment all work exactly as they do for a hand-written component.

## What you need

`SoEx.Workflow.Statecharts`, which wraps [statelyai/xstate-csharp](https://github.com/statelyai/xstate-csharp)
(add `XState.Scxml` too if you author in SCXML — the adapter takes a machine, not a source format, so it does
not depend on that package itself).

That library reads XState **v6** JSON and SCXML, and ships no runtime of its own: a transition is a pure
`(machine, state, event)` to `(state, effects)` function, and the effects are plain data. That is what makes
this work — the durable half is entirely SoEx's, and the machine never needs a scheduler, a timer service or
a mailbox.

**What travels.** The chart does not: it is loaded locally at start and stays there. Only the snapshot crosses
the wire, on the sealed step DTO, and it is fed back into the chart this node already holds — or into the
version that wrote it, if the chart has been revised since the instance parked.

## 1. Write the contract and the component

The contract is the same shape as any other step component: a step DTO, and the data a raise may carry.

```csharp
public interface IApprovalFlow
{
    Task<WorkflowAction> Run(MachineStep step, MachineEventData? data = null);
}

public sealed class ApprovalFlow(StatechartStep chart) : IApprovalFlow
{
    public Task<WorkflowAction> Run(MachineStep step, MachineEventData? data = null) =>
        Task.FromResult(chart.Advance(step, data));
}
```

## 2. Choose a format, and load the chart

Both XState v6 JSON and SCXML work. They are not equivalent, and the difference is about what the chart keeps
in its own state:

| | JSON | SCXML |
|---|---|---|
| Chart state across a park | plain data, survives with nothing extra | the datamodel lives in a JavaScript engine, which cannot be journaled |
| Conditions and assigns | fine | fine, once you say which values to carry |
| What you write | nothing | two lines naming the values that matter |

**JSON is the default.** Its context is plain data, so there is nothing to declare and nothing to get wrong:

```csharp
StateMachine<JsonElement> machine = MachineConfig.FromJson(
    chartJson,
    new MachineImplementations<JsonElement>()
        .Action("recordApprover", args => audit.Record(args.Event))
        .Guard("isManager", args => roles.IsManager(args.Context)));
```

The context is the chart's own `context` JSON, so there is no C# context type to declare.

**SCXML** goes through `ScxmlConverter.Parse` in the separate `XState.Scxml` package. Check it first, because a
chart that keeps values in its datamodel needs a decision:

```csharp
StateMachine<ScxmlDataModel> machine = ScxmlConverter.Parse(ScxmlDurability.RequireDurable(xml));
```

`RequireDurable` accepts a chart that routes on event names alone and refuses one that uses `<data>`,
`<assign>`, `cond`, `<script>` and friends — naming what it found, before a single instance exists, rather than
letting values vanish at the first wait months later. It is a default, not a verdict: a chart that genuinely
needs its datamodel can carry it (see below) and simply does not call this. `DatamodelUse(xml)` answers the
same question without throwing, so you can audit a folder of charts before committing to a format.

**A chart is not self-contained.** Its actions and guards are names; the code behind them is supplied at
import, and a name with no implementation is refused there — every missing one at once, rather than one per
redeploy. The drawing owns the shape of the flow; your code owns what the boxes do.

**Where the chart comes from is a deployment decision with one hard rule: every worker must load a
byte-identical chart, and so must a replay.** If two workers disagree about the machine, the flow diverges. An
embedded resource is the safest default, because it is pinned to the assembly and cannot skew. A file or a
config store is fine if it is versioned and immutable per deploy — never "latest" from somewhere mutable.

Charts are loaded **once, at start**, never per step. A machine is an immutable value with a pure transition
function, so one instance serves every step of every workflow instance; parsing per step would buy nothing and
cost real time. The chart never crosses the wire — only the snapshot does.

## 3. Bind it to the flow

```csharp
var chart = new StatechartStep(machine, new StatechartOptions
{
    // The events an outside caller may raise at a parked machine, in the order they become wait branches.
    ResumableEvents = ["approve", "reject"],
    // The snapshot crosses the journal as JSON, so say how to read the context back.
    ContextConverter = StatechartOptions.JsonContext<JsonElement>(),
});
```

An SCXML chart needs three more, because its context is a live JavaScript engine rather than data:

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

Those lines live in your code, not the library's, so that a consumer who only ever uses JSON never takes a
dependency on a JavaScript engine.

`ResumableEvents` is declared rather than derived. A snapshot does not enumerate what the machine would
accept next, and even if it did, every one of these names is journaled in clear as its runtime's delivery
key — so which names a flow exposes is a decision to make deliberately. Declared order is also the tie-break
when more than one is deliverable at once.

`ContextConverter` is needed whenever the machine's context is not already plain data: the snapshot crosses
the journal as JSON, so an object slot comes back as a dictionary unless you say how to read it.

## 4. Start it

`Seed` produces the first step — the machine's initial snapshot, after its entry actions have run:

```csharp
byte[] seed = step.SealStep(instanceId, chart.Seed("order-42"),
    WorkflowEnvelope.AmbientFor(step.Serializer, SubjectContext.Managed(email)));
await gateway.StartAsync(instanceId, seed);
```

## 5. Move it

A caller raises one of the declared events. Because each wait branch seals its own continuation, the branch
that fires is what tells the next step which event it was — no side-channel:

```csharp
await gateway.RaiseEventAsync(instanceId, "approve",
    sealer.SealEventData(instanceId, new MachineEventData("\"ana\"")));
```

`MachineEventData.Data` is JSON, and by default a JSON primitive reaches the machine as the matching CLR
value while an object or array arrives as a `JsonElement`. Override `EventDataConverter` to deserialize into
something the machine's transitions can pattern-match on.

## Many charts, one step

A chart per flow — its own contract, its own binding — gives each flow its own flow key, and therefore its own
gateway, sealer and authorization policy. Reach for that when the flows are governed differently.

When they are governed alike and there are simply a lot of them, one component can serve them all:

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

The routing key is the machine id the snapshot already carries — a chart names itself in every snapshot it
writes, so there is nothing to keep in step. Start an instance with `router.Seed("approval")`. A snapshot naming
a chart the host does not serve is refused and the message lists what is served, so removing a chart from a
deployment parks its instances loudly instead of routing them somewhere wrong.

The trade-off is the flow key: one binding means one gateway and one authorization policy for every chart in it.

## How the machine maps onto a flow

| The machine | The flow |
|---|---|
| final state (`done`) | `Complete`, carrying the machine's output **as JSON** |
| `error` state | the step throws, so the instance parks with its key retained |
| active, with declared events | `WaitForEvent`, one branch per declared event |
| `after(...)` delayed transition | the wait's durable `Timeout`, resuming into `xstate.timer.<id>` |
| zero-delay self-raise | looped inside the same step, never a durable round-trip |
| entry/exit actions | run inside the step, under its retry and idempotency |

**The result is JSON.** A chart has no CLR output type — its output is whatever JSON it declared — and an
untyped value in the result slot is what an allow-listed serializer refuses to write without being told the
type by name. So a statechart-backed flow completes with its machine's output serialized as JSON, and the
consumer deserializes it. That holds for a machine built in C# too, so the rule is the same either way.

**Timer deadlines.** A snapshot records a timer's *declared delay*, never its deadline. If it did not,
a flow resumed by an unrelated event while a timer was running would re-arm that timer from full — an SLA
you could push out forever by nudging it. So the deadline travels on the step DTO, and the durable timer is
armed with what is left. The arithmetic happens inside the step, off every engine's replay path, and the
delay it produces is journaled once with the step's action.

**Nothing is held between steps.** A `StatechartStep` keeps only what it was composed with — the machine, the
versions, the options — and never per-instance state, so a node accumulates nothing as instances pass through
it. What a single step builds, it releases: see `ReleaseContext` above, which matters most for SCXML, where each
step builds a JavaScript engine that has no reader once the step returns.

**A timer with no declared events.** A wait needs at least one branch, so a machine that is waiting only on
an `after(...)` transition gets a branch named after the timer itself. That name is journaled in clear like
any other, so a caller who knows it can fire the timer early. Declare a resumable event if you would rather
the timer were the only thing that can move the flow.

**What is refused.** Spawning a child, sending to another actor and emitting to subscribers all need an actor
runtime, and the portable flow deliberately has none. Each throws by name rather than being ignored, because
a silently dropped effect leaves a flow that looks like it worked. Model those as workflow events instead.

## Changing a chart under live instances

A snapshot is self-describing: it carries the id and version of the chart that wrote it. Register the versions
that may still appear in storage, and say which one new instances start at:

```csharp
var chart = new StatechartStep(MachineVersions.Create(v1, v2), current: "2.0.0", options);
```

An instance parked under v1 resumes on v1 and finishes there — pin-and-drain, the same policy the rest of the
framework takes to a changed flow, rather than being half-migrated mid-run by a build it never knew about. New
instances start on the current version.

Keep a superseded version registered until the instances running it have drained. Drop it too early and those
instances fail loudly on restore, parking with their keys retained — so re-registering the version recovers
them, rather than losing them or misreading their state.

## Which runtimes this works on

Every runtime that supports the portable flow, with no adapter change — it is a step component, and the
runtimes never see the machine. The per-engine realities are the portable flow's own, not the statechart's:

- **Temporal, Durable Task, InProc** — nothing to know beyond the portable flow's usual behaviour.
- **Elsa** — portable durable timers need a consumer-driven resumer. Until one is wired, a machine parked on
  an `after(...)` transition waits indefinitely. Prefer Temporal for a machine that leans on timers.
- **Restate** — a durable promise is write-once per event name per generation, so a machine that can be
  resumed twice by the *same* event name in one generation cannot be expressed there.
- **Camunda 8 / Zeebe** — native BPMN only, no portable flow, so a machine-backed step component is not
  available. The BPMN graph is the flow on that engine.

See [the runtime matrix](../reference/runtime-matrix.md) for the full comparison.

## A runnable example

[`examples/Statechart`](../../examples/Statechart/README.md) is all of this end to end and needs no backend:
an embedded chart, a data-carrying raise, the chart's own timer escalating a flow, and the crypto-shred at
termination. `dotnet run --project examples/Statechart`.

## See also

- [Write a step component](write-a-step-component.md) — the contract shape this reuses.
- [`WorkflowAction`](../reference/workflow-action.md) — what the mapping produces, including event data.
