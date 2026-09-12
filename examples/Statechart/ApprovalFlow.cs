using SoEx.Workflow;
using SoEx.Workflow.Statecharts;

namespace StatechartDemo;

/// <summary>
/// The consumer's workflow contract. Note what it does NOT say: nothing here mentions a statechart. It is the
/// ordinary two-parameter step contract any component uses — the step DTO the flow threads forward, and the
/// data a raise may carry — which is exactly why a chart-backed flow inherits the governed-step seam whole.
/// </summary>
public interface IApprovalFlow
{
    Task<WorkflowAction> Run(MachineStep step, MachineEventData? data = null);
}

/// <summary>
/// The whole component. It holds the chart that was loaded at start and hands each step to it; every decision
/// about what happens next belongs to the chart, and every decision about how that survives a restart belongs
/// to the framework.
/// <para>
/// <see cref="IErasureEvent"/> is mandatory for a workflow-hosted entrypoint — the composition refuses an
/// entrypoint without it rather than letting the termination degrade to a silent no-op. Here the flow keeps no
/// data of its own outside the sealed journal, so the hooks have nothing to extract; that is written out
/// explicitly rather than left to be inferred.
/// </para>
/// </summary>
public sealed class ApprovalFlow(StatechartStep chart) : IApprovalFlow, IErasureEvent
{
    public Task<WorkflowAction> Run(MachineStep step, MachineEventData? data = null) =>
        Task.FromResult(chart.Advance(step, data));

    public Task OnRetaining(RetainingContext context) => Task.CompletedTask;

    public Task OnTerminated(TerminatedContext context) => Task.CompletedTask;

    public Task OnRetentionHeld(RetentionHeldContext context) => Task.CompletedTask;
}
