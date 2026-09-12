using System.Text.Json;
using XState.Persistence;

namespace SoEx.Workflow.Statecharts;

/// <summary>
/// One step component for MANY charts. Every chart is loaded at start and registered here by its machine id;
/// each step is dispatched to the chart that wrote its snapshot, because the snapshot says which chart it is.
/// <para>
/// This is the alternative to a contract and a binding per chart. Both are legitimate, and the choice is
/// about hosting rather than correctness:
/// </para>
/// <list type="bullet">
/// <item>
/// A binding per chart gives each flow its own flow key, and therefore its own gateway, sealer and
/// authorization policy. Reach for it when the flows are governed differently from one another.
/// </item>
/// <item>
/// One binding for all charts — this type — is one registration no matter how many charts there are, which is
/// what you want when a client hands you a folder of them. The cost is that they share one flow key, so they
/// share the gateway and its authorization; per-flow policy is no longer expressible.
/// </item>
/// </list>
/// <para>
/// Instance ids still separate the runs, so two charts can be in flight for the same subject without
/// interfering. Idempotency is unaffected: its key is the instance and the sequence, not the chart.
/// </para>
/// </summary>
public sealed class StatechartRouter
{
    private readonly IReadOnlyDictionary<string, StatechartStep> _charts;
    private readonly JsonSerializerOptions _snapshotJson = SnapshotJson.DefaultOptions();

    /// <param name="charts">Every chart this component serves, keyed by its machine id.</param>
    public StatechartRouter(IReadOnlyDictionary<string, StatechartStep> charts)
    {
        ArgumentNullException.ThrowIfNull(charts);
        if (charts.Count == 0)
        {
            throw new ArgumentException("a router needs at least one chart", nameof(charts));
        }

        _charts = charts;
    }

    /// <summary>Seeds a new instance of the named chart.</summary>
    public MachineStep Seed(string machineId, object? input = null) => Chart(machineId).Seed(input);

    /// <summary>
    /// Advances whichever chart wrote this step's snapshot.
    /// <para>
    /// The routing key is the machine id the snapshot carries, not anything the framework adds: a chart names
    /// itself in every snapshot it writes, so there is nothing to keep in step and nothing to get wrong. A
    /// snapshot naming a chart this component does not serve is refused rather than guessed at.
    /// </para>
    /// </summary>
    public WorkflowAction Advance(MachineStep step, MachineEventData? data = null)
    {
        ArgumentNullException.ThrowIfNull(step);
        return Chart(MachineIdOf(step)).Advance(step, data);
    }

    /// <summary>The machine id a step's snapshot names — which chart it belongs to.</summary>
    public string MachineIdOf(MachineStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        // Read through the library's own snapshot shape rather than by poking at property names, so the
        // routing key cannot drift from however the snapshot is actually written.
        PersistedSnapshot snapshot = JsonSerializer.Deserialize<PersistedSnapshot>(step.Snapshot, _snapshotJson)
            ?? throw new InvalidOperationException("the machine step carried no readable snapshot");

        return snapshot.Machine?.Id
            ?? throw new InvalidOperationException(
                "the snapshot names no machine, so there is nothing to route it by — a router needs charts that " +
                "identify themselves, which every chart this library writes does");
    }

    private StatechartStep Chart(string machineId) =>
        _charts.TryGetValue(machineId, out StatechartStep? chart)
            ? chart
            : throw new InvalidOperationException(
                $"no chart named '{machineId}' is registered with this component; it serves: " +
                $"{string.Join(", ", _charts.Keys.Order(StringComparer.Ordinal))}");
}
