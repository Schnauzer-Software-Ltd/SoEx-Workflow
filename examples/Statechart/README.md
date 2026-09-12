# Example — a statechart as a Manager's process

An expense-approval **Manager** whose process is a statechart: drawn in a statechart tool, exported as XState
v6 JSON, and run as a governed SoEx workflow.

```
dotnet run --project examples/Statechart
```

No backend needed: it runs on the in-process runtime.

## The layout is the point

```
Component/                                  ← ALL the business logic is in here
  Manager/Expense/
    Interface/IExpenseManager.cs            the manager's contract: one portable step operation
    Service/ExpenseManager.cs               the manager: hands each step to its process
    Service/expense-approval.chart.json     the process, as drawn — it is the manager's orchestration
    Service/ExpenseApprovalChart.cs         loads the process, binds its named actions to component calls
  Access/Notification/
    Interface/INotificationAccess.cs        telling the claimant what happened
    Service/NotificationAccess.cs

Hosts/InProc/Program.cs                     ← framework wiring ONLY: runtime, stores, transport, composition
```

Everything outside `Component/` is plumbing. Nothing in `Hosts/` decides what the business does — the process
is the manager's chart, and the work each step performs is the manager's call into a component.

Note where the chart lives: beside the manager, not with the host. It is the manager's orchestration
externalised, so it belongs to the manager, and it is an embedded resource so that every worker loads
identical bytes — a chart that differed between nodes would make the flow itself diverge.

## What it shows

```
process  expense-approval  (embedded with the manager, loaded once)

── approved by a manager who is named in the raise
   claim-37f12b27   key live? True
   outcome  {"outcome":"approved"}
   notified claim-37f12b27: approved by ana
   key live after completion? False  (false = journal crypto-shredded)

── nobody approves, the process escalates itself, then approved
   claim-d735405c   key live? True
   outcome  {"outcome":"approved"}
   notified claim-d735405c: escalated
   notified claim-d735405c: approved by the duty manager
   key live after completion? False  (false = journal crypto-shredded)
```

- **The chart never crosses the wire.** It is loaded once at start; only a claim's snapshot travels, sealed
  under the per-claim key like any other step payload.
- **The raise carries data.** Who approved is knowable only to the approver, so it travels with the event and
  reaches the chart as the event's payload. The chart still decides what happens next.
- **The process escalates itself.** Nothing in the host schedules that; the chart's own delayed transition
  becomes the wait's durable timer. (InProc's timer is virtual and the program advances the clock, which is
  what makes a 72-hour process testable in milliseconds. On Durable Task, Temporal, Elsa or Restate it is a
  real durable timer and that line is absent.)
- **The key is destroyed at termination**, so everything the journal still holds for a finished claim is
  unrecoverable. The manager got that by being an ordinary governed manager.

## What is not here

One process, one runtime, so the shape stays readable. The how-to covers choosing between JSON and SCXML and
what SCXML costs, serving several processes from one manager, and keeping old process versions registered
while the claims that started on them drain.

See [Drive a flow with a statechart](../../docs/how-to/drive-a-flow-with-a-statechart.md).
