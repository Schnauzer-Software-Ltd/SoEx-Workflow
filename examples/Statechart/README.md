# Example — a statechart as a governed workflow

A chart drawn in a statechart tool, exported as XState v6 JSON, shipped as an embedded resource, and run
as a SoEx workflow.

```
dotnet run --project examples/Statechart
```

No backend needed: it runs on the in-process runtime.

## What it shows

```
chart    expense-approval v1.0.0  (embedded, loaded once at start)

── approved by a raise that says who
   instance expense-d81784f1   key live? True
   result   {"outcome":"approved"}
   notified: approved by ana
   key live after completion? False  (false = journal crypto-shredded)

── escalated by the chart's own timer, then approved
   instance expense-3d9c610f   key live? True
   result   {"outcome":"approved"}
   escalated: nobody approved before the chart's timer fired
   notified: approved by the duty manager
   key live after completion? False  (false = journal crypto-shredded)
```

Four things worth watching for:

- **The chart is loaded once, at start, and never crosses the wire.** Only the machine's snapshot travels,
  sealed under the per-instance key like any other step payload, and it is fed back into the chart this
  process already holds.
- **The raise carries data.** Who approved is knowable only to the raiser, so it travels with the event and
  reaches the chart as the event's payload — the chart still decides what happens next.
- **The chart's own `after` transition becomes a durable timer.** Nothing here schedules anything; the flow
  escalates because the machine said it should. (On the in-process runtime that timer is virtual and the
  program advances the clock, which is what makes a 72-hour flow testable in milliseconds. On Durable Task,
  Temporal, Elsa or Restate it is a real durable timer and that line is simply absent.)
- **The key is destroyed at termination.** Everything the journal still holds for a completed instance is
  unrecoverable — the crypto-shred, which the chart gets for free by being an ordinary step component.

## The files

| | |
|---|---|
| `approval.chart.json` | the chart, as a tool would export it — an embedded resource, so every worker loads identical bytes |
| `ApprovalFlow.cs` | the whole component: an ordinary two-parameter step contract that says nothing about statecharts |
| `Program.cs` | loading the chart, binding the named actions, the SoEx composition, and two runs |

## What is not here

This example keeps to one chart on one runtime so the shape is easy to read. The how-to covers the rest:
choosing between JSON and SCXML and what SCXML costs, serving many charts from one component, and keeping
old chart versions registered while instances that started on them drain.

See [Drive a flow with a statechart](../../docs/how-to/drive-a-flow-with-a-statechart.md).
