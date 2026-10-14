using System.Reflection;
using System.Text.Json;
using SoEx.Workflow.Statecharts;
using XState;
using XState.Json;

namespace StatechartDemo.Manager.Expense.Service;

/// <summary>
/// The expense-approval process, and the code its steps run.
/// <para>
/// The process itself is drawn in a statechart tool and lives beside this file as
/// <c>expense-approval.chart.json</c> — it is the manager's orchestration, externalised, so it belongs with the
/// manager rather than with a host. The chart names its calls, guards and actions; this is where those names are
/// bound to the component calls behind them. A name the chart uses and nobody implements is refused when the
/// chart is loaded, so the drawing and the code cannot drift apart quietly.
/// </para>
/// </summary>
public static class ExpenseApprovalChart
{
    /// <summary>The events an outside caller may raise at a parked claim, in the order they are offered.</summary>
    public static readonly string[] ResumableEvents = ["approve", "reject"];

    /// <summary>
    /// Loads the process and binds its names to the code behind them. Called once, at start: a machine is an
    /// immutable value, so one instance serves every step of every claim.
    /// <para>
    /// The chart captures no component. A SoEx component is resolved per call, so the manager receives its
    /// components for each call and hands them to the step as <see cref="ExpenseComponents"/>; every call and
    /// action below reaches them from there.
    /// </para>
    /// </summary>
    public static StatechartStep Load()
    {
        StateMachine<JsonElement> machine = MachineConfig.FromJson(
            ReadChart(),
            new MachineImplementations<JsonElement>()
                // A call: the chart's next state depends on what the engine answers, so the review is an invoke
                // and its onDone guards read the answer. An entry action could not do this — the machine picks
                // its transition before any action runs.
                .ActorLogic("reviewBudget", StatechartCall.Of<JsonElement, ExpenseComponents>(async (call, components, ct) =>
                    await components.Budget.ReviewAsync(
                        ClaimId(call.Context), call.Context.GetProperty("amount").GetDecimal())))
                .Guard("overBudget", args => Verdict(args.Event) == "over-budget")
                .Guard("withinLimit", args => Verdict(args.Event) == "within-limit")
                // Actions: work whose result the chart does not branch on. They await their component.
                .Action("notifyApproved", Notify("approved"))
                .Action("notifyRejected", Notify("rejected"))
                .Action("notifyEscalated", Notify("escalated")));

        return new StatechartStep(machine, new StatechartOptions
        {
            ResumableEvents = ResumableEvents,
            ContextConverter = StatechartOptions.JsonContext<JsonElement>(),
        });
    }

    /// <summary>The chart's id, which is also the machine id every snapshot of this process names.</summary>
    public static string ProcessId => "expense-approval";

    // The engine's answer is the output of the invoke's completion event.
    private static string? Verdict(MachineEvent evt) => (evt as DoneActorEvent)?.Output as string;

    // An action the chart names. It runs inside the governed step, so it is covered by the step's retry and
    // idempotency — which is why the notifier is idempotent on the claim and the outcome.
    private static Action<ActionArgs<JsonElement>> Notify(string outcome) =>
        StatechartAction.Of<JsonElement, ExpenseComponents>((args, components, ct) =>
        {
            // Who decided is in the event that took the transition: a person's raise carries their name, and the
            // budget review's completion is the review itself.
            string? by = args.Event switch
            {
                NamedEvent { Data: { } payload } => payload.ToString(),
                DoneActorEvent => "the budget review",
                _ => null,
            };

            return components.Notifications.NotifyAsync(ClaimId(args.Context), outcome, by);
        });

    private static string ClaimId(JsonElement context) =>
        context.TryGetProperty("claimId", out JsonElement id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()!
            : "unknown-claim";

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
