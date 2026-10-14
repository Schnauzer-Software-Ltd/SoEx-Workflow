using XState;

namespace SoEx.Workflow.Statecharts;

/// <summary>
/// An asynchronous action: a named chart action whose implementation awaits, such as a notification sent
/// through a component. Register it under the name the chart uses:
/// <c>.Action("notifyApproved", StatechartAction.Of&lt;JsonElement&gt;(async (args, ct) =&gt; ...))</c>.
/// <para>
/// The step awaits it in its place among the transition's other effects. The next effect starts only after
/// it completes. An exception from it fails the step, so the runtime retries the step. Make it idempotent.
/// </para>
/// <para>
/// An action cannot change which transition the machine takes. When the next state depends on what the work
/// returns, use a <see cref="StatechartCall"/>.
/// </para>
/// </summary>
public static class StatechartAction
{
    // The library runs an action through a synchronous closure. This closure does no work itself: it hands
    // the work to the step that is running it, and the step awaits it. The step runs each action's closure
    // synchronously and collects the work before it awaits, so one slot per thread is sufficient.
    [ThreadStatic]
    private static List<Func<CancellationToken, Task>>? _collector;

    [ThreadStatic]
    private static object? _services;

    /// <summary>An action that awaits.</summary>
    public static Action<ActionArgs<TContext>> Of<TContext>(Func<ActionArgs<TContext>, CancellationToken, Task> run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return args =>
        {
            List<Func<CancellationToken, Task>> collector = _collector ?? throw OutsideAStep();
            collector.Add(ct => run(args, ct));
        };
    }

    /// <summary>
    /// An action that awaits and uses the services the step was given: the <c>services</c> argument of
    /// <see cref="StatechartStep.AdvanceAsync"/>. Use it to reach the components the Manager received for this
    /// call.
    /// </summary>
    public static Action<ActionArgs<TContext>> Of<TContext, TServices>(
        Func<ActionArgs<TContext>, TServices, CancellationToken, Task> run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return args =>
        {
            List<Func<CancellationToken, Task>> collector = _collector ?? throw OutsideAStep();

            // Read now, while the step that runs this action has them in place. The work itself runs after
            // the step has put the slot back.
            TServices services = StatechartCall.ServicesOf<TServices>(_services);
            collector.Add(ct => run(args, services, ct));
        };
    }

    private static InvalidOperationException OutsideAStep() => new(
        "an asynchronous statechart action ran outside a StatechartStep. A StatechartStep awaits it; " +
        "a caller that runs ActionEffect.Exec itself has nothing to await it with.");

    // Runs one action effect through the binding's RunAction and returns the asynchronous work it deferred,
    // in order. Synchronous actions defer nothing and return an empty list.
    internal static List<Func<CancellationToken, Task>> Collect(
        Action<ActionEffect> runAction, ActionEffect action, object? services)
    {
        List<Func<CancellationToken, Task>>? outer = _collector;
        object? outerServices = _services;
        var collected = new List<Func<CancellationToken, Task>>();
        _collector = collected;
        _services = services;
        try
        {
            runAction(action);
        }
        finally
        {
            _collector = outer;
            _services = outerServices;
        }

        return collected;
    }
}
