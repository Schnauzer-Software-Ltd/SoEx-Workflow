using SoEx.Workflow;
using SoEx.Workflow.Statecharts;
using StatechartDemo.Manager.Expense.Interface;

namespace StatechartDemo.Manager.Expense.Service;

/// <summary>
/// The expense-approval manager. It orchestrates nothing by hand: the process is the chart
/// (<see cref="ExpenseApprovalChart"/>), and each step hands the flow's state to it and returns what it says
/// should happen next.
/// <para>
/// <see cref="IErasureEvent"/> is mandatory for a workflow-hosted manager — the composition refuses one
/// without it rather than letting the termination degrade to a silent no-op. This manager keeps nothing
/// outside the sealed journal, so the hooks have nothing to extract; that is written out rather than inferred.
/// A manager that DID hold data elsewhere would extract it in <c>OnRetaining</c>, while the key is still live.
/// </para>
/// </summary>
public sealed class ExpenseManager(StatechartStep process) : IExpenseManager, IErasureEvent
{
    public Task<WorkflowAction> Approve(MachineStep step, MachineEventData? data = null) =>
        Task.FromResult(process.Advance(step, data));

    /// <summary>Pre-shred extract; fires while the journal is still readable, on every termination path.</summary>
    public Task OnRetaining(RetainingContext context) => Task.CompletedTask;

    public Task OnTerminated(TerminatedContext context) => Task.CompletedTask;

    public Task OnRetentionHeld(RetentionHeldContext context) => Task.CompletedTask;
}
