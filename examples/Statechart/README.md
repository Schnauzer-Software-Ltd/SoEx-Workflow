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
    Service/ExpenseApprovalChart.cs         loads the process, binds its named calls and actions to components
    Service/ExpenseComponents.cs            the components the manager hands to the process on each step
  Engine/Budget/
    Interface/IBudgetEngine.cs              the expense policy: within limit, needs approval, or over budget
    Service/BudgetEngine.cs
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

── a small claim: the budget review approves it, and nobody is asked
   claim-b0888fdb   amount 80   key live? True
   notified claim-b0888fdb: approved by the budget review
   outcome  {"outcome":"approved"}
   key live after completion? False  (false = journal crypto-shredded)

── approved by a manager who is named in the raise
   claim-be57bbfb   amount 450   key live? True
   notified claim-be57bbfb: approved by ana
   outcome  {"outcome":"approved"}
   key live after completion? False  (false = journal crypto-shredded)

── nobody approves, the process escalates itself, then approved
   claim-52b8e8e7   amount 450   key live? True
   notified claim-52b8e8e7: escalated
   notified claim-52b8e8e7: approved by the duty manager
   outcome  {"outcome":"approved"}
   key live after completion? False  (false = journal crypto-shredded)

── over budget: the budget review rejects it, and nobody is asked
   claim-c889fe66   amount 5000   key live? True
   notified claim-c889fe66: rejected by the budget review
   outcome  {"outcome":"rejected"}
   key live after completion? False  (false = journal crypto-shredded)
```

- **The answer of a component chooses the next state.** The first state of the chart invokes the budget
  review, a call to the Engine. The guards on `onDone` read the answer of the Engine. A small claim goes to
  `approved` and a large claim goes to `rejected`, in the same step and with no person. Other claims wait
  for a manager. The review runs in the first governed step. The seed runs nothing.
- **The notifications are asynchronous actions.** Each one awaits its component. The step awaits each
  action before the next effect.
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

The notifications appear when each step runs. They do not appear together at the end. SoEx resolves the
Manager and each component for each call, and a component holds no data between calls. The Manager gets
the Engine and the Access through proxies, as constructor parameters. It passes them to the process as
`ExpenseComponents` on each step. The chart holds no component. A value that a component keeps in a field
is lost before the next step. Keep state that must continue in the sealed journal or in an injected
store.

## Related topics

This example has one process and one runtime, so the shape is easy to read. The how-to guide covers these
topics:

- how to select JSON or SCXML, and the cost of SCXML
- how to serve several processes from one Manager
- how to report a business failure from a call, and keep the result of a call for a later state
- how to loop a state on a timer
- how to keep old process versions registered until the claims that started on them drain

See [Drive a flow with a statechart](../../docs/how-to/drive-a-flow-with-a-statechart.md).
