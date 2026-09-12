using System.Reflection;
using System.Text.Json;
using SoEx.Workflow.Statecharts;
using StatechartDemo.Access.Notification.Interface;
using XState;
using XState.Json;

namespace StatechartDemo.Manager.Expense.Service;

/// <summary>
/// The expense-approval process, and the code its steps run.
/// <para>
/// The process itself is drawn in a statechart tool and lives beside this file as
/// <c>expense-approval.chart.json</c> — it is the manager's orchestration, externalised, so it belongs with the
/// manager rather than with a host. The chart names its actions; this is where those names are bound to the
/// component calls behind them. A name the chart uses and nobody implements is refused when the chart is
/// loaded, so the drawing and the code cannot drift apart quietly.
/// </para>
/// </summary>
public static class ExpenseApprovalChart
{
    /// <summary>The events an outside caller may raise at a parked claim, in the order they are offered.</summary>
    public static readonly string[] ResumableEvents = ["approve", "reject"];

    /// <summary>
    /// Loads the process and binds its actions to the components behind them. Called once, at start: a machine
    /// is an immutable value, so one instance serves every step of every claim.
    /// <para>
    /// The components are reached through a factory rather than captured as instances, because a SoEx component
    /// is resolved per call. Binding one instance here would quietly give the whole process a component that
    /// outlives every invocation — which works until something in it holds state, and then stops working in a
    /// way that is hard to see.
    /// </para>
    /// </summary>
    public static StatechartStep Load(Func<INotificationAccess> notifications)
    {
        ArgumentNullException.ThrowIfNull(notifications);

        StateMachine<JsonElement> machine = MachineConfig.FromJson(
            ReadChart(),
            new MachineImplementations<JsonElement>()
                .Action("notifyApproved", args => Notify(notifications, args, "approved"))
                .Action("notifyRejected", args => Notify(notifications, args, "rejected"))
                .Action("notifyEscalated", args => Notify(notifications, args, "escalated")));

        return new StatechartStep(machine, new StatechartOptions
        {
            ResumableEvents = ResumableEvents,
            ContextConverter = StatechartOptions.JsonContext<JsonElement>(),
        });
    }

    /// <summary>The chart's id, which is also the machine id every snapshot of this process names.</summary>
    public static string ProcessId => "expense-approval";

    // An action the chart names. It runs inside the governed step, so it is covered by the step's retry and
    // idempotency — which is why the notifier is idempotent on the claim and the outcome.
    private static void Notify(Func<INotificationAccess> notifications, ActionArgs<JsonElement> args, string outcome)
    {
        string claimId = args.Context.TryGetProperty("claimId", out JsonElement id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()!
            : "unknown-claim";

        // Who acted is knowable only to whoever raised the event, so it arrives as the event's payload.
        string? by = args.Event is NamedEvent { Data: { } payload } ? payload.ToString() : null;

        notifications().NotifyAsync(claimId, outcome, by).GetAwaiter().GetResult();
    }

    // The chart ships WITH the assembly: every worker loads identical bytes and no deploy can skew one node's
    // copy against another's, which matters because a divergent machine would diverge the flow.
    private static string ReadChart()
    {
        Assembly assembly = typeof(ExpenseApprovalChart).Assembly;
        string name = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("expense-approval.chart.json", StringComparison.Ordinal));

        using Stream stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
