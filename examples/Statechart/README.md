> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Example: a statechart as the process of a Manager

This example is an expense-approval **Manager**. Its process is a statechart. You draw the statechart in
a statechart tool and export it as XState v6 JSON. SoEx runs it as a governed workflow.

```
dotnet run --project examples/Statechart
```

The example runs on the InProc runtime. It needs no runtime server.

## The layout

The layout is the main lesson of this example.

```
Component/                                  ← ALL the business logic is in here
  Manager/Expense/
    Interface/IExpenseManager.cs            the manager's contract: one portable step operation
    Service/ExpenseManager.cs               the manager: hands each step to its process
    Service/expense-approval.chart.json     the process, as drawn — it is the manager's orchestration
    Service/ExpenseApprovalChart.cs         loads the process, binds its named actions to component calls
  Access/Notification/
    Interface/INotificationAccess.cs        telling the claimant what happened — emits, keeps nothing
    Service/NotificationAccess.cs

Hosts/InProc/Program.cs                     ← framework wiring ONLY: runtime, stores, transport, composition
```

All code outside `Component/` is plumbing. The business decisions are in `Component/`. The process is the
chart of the Manager. The work of each step is a call from the Manager into a component. `Hosts/` holds
no business decisions.

The chart is next to the Manager. It is the orchestration of the Manager, in a separate file, so it
belongs to the Manager. The chart is an embedded resource. Thus each worker loads the same bytes. If the
chart were different on two nodes, the flow would also be different on them.

## What it shows

```
process  expense-approval  (embedded with the manager, loaded once)

── approved by a manager who is named in the raise
   claim-6d3f85b9   key live? True
   notified claim-6d3f85b9: approved by ana
   outcome  {"outcome":"approved"}
   key live after completion? False  (false = journal crypto-shredded)

── nobody approves, the process escalates itself, then approved
   claim-4da8890e   key live? True
   notified claim-4da8890e: escalated
   notified claim-4da8890e: approved by the duty manager
   outcome  {"outcome":"approved"}
   key live after completion? False  (false = journal crypto-shredded)
```

- **The chart stays in the process.** The example loads the chart one time, at start. Only the snapshot of
  a claim goes across the wire. The per-claim key seals the snapshot, the same as each other step payload.
- **The raise contains data.** Only the approver knows who approved. Thus this value goes with the event
  and gets to the chart as the payload of the event. The chart then decides the next step.
- **The process escalates itself.** The host schedules no escalation. The delayed transition of the chart
  becomes the durable timer of the wait. On InProc, the timer is virtual and the program moves the clock
  forward. Thus you can test a 72-hour process in milliseconds. On Durable Task, Temporal, Elsa, or
  Restate, the timer is a real durable timer, and that line is absent.
- **Termination destroys the key.** After termination, all data that the journal holds for a finished
  claim is unrecoverable. The Manager gets this behavior because it is a usual governed Manager.

The notifications appear when each step runs. They do not appear together at the end. SoEx resolves a
component for each call, and the component holds no data between calls. Thus the actions of the chart get
to their components through a factory. They do not capture an instance at load. A value that a component
keeps in a field is lost before the next step. Keep state that must continue in the sealed journal or in
an injected store.

## Related topics

This example has one process and one runtime, so the shape is easy to read. The how-to guide covers these
topics:

- how to select JSON or SCXML, and the cost of SCXML
- how to serve several processes from one Manager
- how to keep old process versions registered until the claims that started on them drain

See [Drive a flow with a statechart](../../docs/how-to/drive-a-flow-with-a-statechart.md).
