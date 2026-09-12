using SoEx.Workflow;
using SoEx.Workflow.Statecharts;

namespace StatechartDemo.Manager.Expense.Interface;

/// <summary>
/// The expense-approval manager, portable flow. One step operation, exactly as any other portable manager
/// has: the step DTO the flow threads forward, and the data a raise may carry.
/// <para>
/// That the process behind it is a statechart is not visible here, and deliberately so — a chart-backed
/// manager is an ordinary workflow manager, which is why it inherits the sealed journal, the crypto-shred,
/// the PII guards and idempotency without asking for any of them.
/// </para>
/// </summary>
public interface IExpenseManager
{
    /// <summary>Expense approval — portable.</summary>
    Task<WorkflowAction> Approve(MachineStep step, MachineEventData? data = null);
}
