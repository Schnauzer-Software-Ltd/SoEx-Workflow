using System.Diagnostics.Metrics;

namespace SoEx.Workflow;

/// <summary>
/// The framework's observability surface — a <see cref="System.Diagnostics.Metrics"/> meter an operator wires
/// to any OpenTelemetry / metrics backend to answer "how many instances are stuck / held / awaiting erasure"
/// without writing code against in-process store APIs. Emission is opt-in: the governed components take a
/// nullable <see cref="WorkflowMetrics"/> and no-op when it is absent, so the core stays hosting-free. A
/// consumer creates one, threads it into the composition root, subscribes to the meter named
/// <see cref="MeterName"/>, and (optionally) calls <see cref="TrackBacklog"/> with the pending-erasure store.
/// <para>The instrument names below are the stable public contract — dashboards and alerts bind to them.</para>
/// </summary>
public sealed class WorkflowMetrics : IDisposable
{
    /// <summary>The meter name to subscribe to (e.g. <c>.WithMetrics(m =&gt; m.AddMeter(WorkflowMetrics.MeterName))</c>).</summary>
    public const string MeterName = "SoEx.Workflow";

    private readonly Meter _meter;
    private readonly Counter<long> _stepsExecuted;
    private readonly Counter<long> _stepsFailed;
    private readonly Counter<long> _shreds;             // tag: outcome = complete | held
    private readonly Counter<long> _sweptInstances;     // tag: state = complete | held | unresolved
    private readonly Counter<long> _deadlineEscalations;

    public WorkflowMetrics(IMeterFactory? meterFactory = null)
    {
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _stepsExecuted = _meter.CreateCounter<long>(
            "soex.workflow.steps.executed", unit: "{step}", description: "Governed steps dispatched successfully.");
        _stepsFailed = _meter.CreateCounter<long>(
            "soex.workflow.steps.failed", unit: "{step}", description: "Governed step dispatches that threw (per attempt).");
        _shreds = _meter.CreateCounter<long>(
            "soex.workflow.shreds", unit: "{instance}", description: "Termination outcomes; tag outcome=complete|held.");
        _sweptInstances = _meter.CreateCounter<long>(
            "soex.workflow.sweep.instances", unit: "{instance}",
            description: "Instances handled by an erasure sweep pass; tag state=complete|held|unresolved.");
        _deadlineEscalations = _meter.CreateCounter<long>(
            "soex.workflow.erasure.deadline_escalations", unit: "{request}",
            description: "Erasure requests whose statutory deadline was escalated.");
    }

    public void StepExecuted() => _stepsExecuted.Add(1);
    public void StepFailed() => _stepsFailed.Add(1);
    public void ShredCompleted() => _shreds.Add(1, new KeyValuePair<string, object?>("outcome", "complete"));
    public void ShredHeld() => _shreds.Add(1, new KeyValuePair<string, object?>("outcome", "held"));
    public void DeadlineEscalated() => _deadlineEscalations.Add(1);

    /// <summary>Records a swept instance under its resulting <see cref="ErasureState"/> (complete/held/unresolved).</summary>
    public void Swept(ErasureState state) =>
        _sweptInstances.Add(1, new KeyValuePair<string, object?>("state", state switch
        {
            ErasureState.Complete => "complete",
            ErasureState.Held => "held",
            _ => "unresolved",
        }));

    /// <summary>
    /// Registers observable gauges for the pending-erasure backlog — the count of open requests and the age of
    /// the oldest — so an operator can alert on a backstop that has stopped draining while statutory deadlines
    /// breach. Call once with the live pending store; the gauges read it lazily on each metrics collection.
    /// </summary>
    public void TrackBacklog(IPendingErasureRequests pending, TimeProvider? time = null)
    {
        TimeProvider clock = time ?? TimeProvider.System;
        _meter.CreateObservableGauge(
            "soex.workflow.erasure.backlog.count", () => (long)pending.Backlog().Count,
            unit: "{request}", description: "Open erasure requests awaiting a drain.");
        _meter.CreateObservableGauge(
            "soex.workflow.erasure.backlog.oldest_age.seconds",
            () => pending.Backlog().OldestReceivedAt is { } received ? (clock.GetUtcNow() - received).TotalSeconds : 0d,
            unit: "s", description: "Age of the oldest un-drained erasure request.");
    }

    public void Dispose() => _meter.Dispose();
}
