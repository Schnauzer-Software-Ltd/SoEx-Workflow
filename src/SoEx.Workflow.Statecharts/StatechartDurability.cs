using System.Reflection;
using XState;
using XState.Persistence;

namespace SoEx.Workflow.Statecharts;

/// <summary>
/// Checks a machine for constructs that cannot run correctly as a durable flow. A
/// <see cref="StatechartStep"/> runs this check when you create it, so a chart with one of these problems
/// is refused at start, before an instance exists.
/// <para>
/// It refuses:
/// </para>
/// <list type="bullet">
/// <item>
/// A delayed (<c>after</c>) transition that targets its own state without <c>"reenter": true</c>. A
/// transition that does not re-enter its state does not run the entry actions again and does not restart
/// the state's timers. The loop runs one time and then has no timer.
/// </item>
/// <item>An <c>invoke</c> whose source is not a <see cref="StatechartCall"/>. A durable flow has no
/// runtime for child machines or other actors.</item>
/// <item>An <c>invoke</c> with a <c>timeout</c>, <c>onTimeout</c> or <c>onSnapshot</c>. A call completes
/// inside the step that starts it, so these have no meaning.</item>
/// <item>An <c>invoke</c> whose last <c>onDone</c> transition has a guard. If no guard passes, the result is
/// ignored and the machine stays in the invoking state with nothing to move it on. End the list with a
/// transition that has no guard.</item>
/// </list>
/// </summary>
public static class StatechartDurability
{
    /// <summary>Every problem the check finds, one line each. Empty when the machine is suitable.</summary>
    public static IReadOnlyList<string> Problems(IMachineLogic machine)
    {
        ArgumentNullException.ThrowIfNull(machine);

        Type? contextType = ContextTypeOf(machine.GetType());
        if (contextType is null)
        {
            // A machine that is not a StateMachine<TContext> has no states to walk.
            return [];
        }

        return (IReadOnlyList<string>)ProblemsOfMethod.MakeGenericMethod(contextType).Invoke(null, [machine])!;
    }

    /// <summary>Returns <paramref name="machine"/> when it is suitable, and throws with every problem when not.</summary>
    public static T Require<T>(T machine) where T : IMachineLogic
    {
        IReadOnlyList<string> problems = Problems(machine);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                $"machine '{machine.Id}' (version {machine.Version ?? "none"}) cannot run as a durable flow:" +
                Environment.NewLine + "  - " + string.Join(Environment.NewLine + "  - ", problems));
        }

        return machine;
    }

    /// <summary>Checks each version in a registry.</summary>
    public static MachineVersions Require(MachineVersions versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        foreach (string version in versions.Versions)
        {
            if (versions.Get(version) is { } machine)
            {
                Require(machine);
            }
        }

        return versions;
    }

    // Whether each invoke id has onError transitions. Null for an id that two invokes share with different
    // answers, where the step cannot tell which one a failure came from.
    internal static IReadOnlyDictionary<string, bool?> OnErrorByInvoke(IMachineLogic machine)
    {
        Type? contextType = ContextTypeOf(machine.GetType());
        return contextType is null
            ? new Dictionary<string, bool?>()
            : (IReadOnlyDictionary<string, bool?>)OnErrorOfMethod.MakeGenericMethod(contextType).Invoke(null, [machine])!;
    }

    private static readonly MethodInfo OnErrorOfMethod =
        typeof(StatechartDurability).GetMethod(nameof(OnErrorOf), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static IReadOnlyDictionary<string, bool?> OnErrorOf<TContext>(StateMachine<TContext> machine)
    {
        var byId = new Dictionary<string, bool?>(StringComparer.Ordinal);
        Visit(machine.Root);
        return byId;

        void Visit(StateNode<TContext> node)
        {
            foreach (InvokeDefinition<TContext> invoke in node.Invokes)
            {
                bool has = invoke.OnError.Count > 0;
                byId[invoke.Id] = byId.TryGetValue(invoke.Id, out bool? seen) && seen != has ? null : has;
            }

            foreach (StateNode<TContext> child in node.Children)
            {
                Visit(child);
            }
        }
    }

    private static readonly MethodInfo ProblemsOfMethod =
        typeof(StatechartDurability).GetMethod(nameof(ProblemsOf), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static Type? ContextTypeOf(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(StateMachine<>)
            ? type.GetGenericArguments()[0]
            : null;

    private static IReadOnlyList<string> ProblemsOf<TContext>(StateMachine<TContext> machine)
    {
        var problems = new List<string>();
        Walk(machine.Root, problems);
        return problems;
    }

    private static void Walk<TContext>(StateNode<TContext> node, List<string> problems)
    {
        foreach ((_, TransitionDefinition<TContext> transition) in node.After)
        {
            if (!transition.Reenter && transition.Targets.Any(target => ReferenceEquals(target, node)))
            {
                problems.Add(
                    $"state '{node.Path}' has a delayed transition to itself that does not re-enter it, so the " +
                    "state's entry actions and timers do not run again and the loop runs one time. Add " +
                    "\"reenter\": true to the transition. To run actions after a delay and stay, remove the target.");
            }
        }

        foreach (InvokeDefinition<TContext> invoke in node.Invokes)
        {
            if (invoke.Logic is not StatechartCall)
            {
                problems.Add(
                    $"state '{node.Path}' invokes '{invoke.Src ?? invoke.Id}', which is a " +
                    $"{invoke.Logic.GetType().Name}. A durable flow runs only a {nameof(StatechartCall)} as an " +
                    "invoke. Register the source as a StatechartCall.");
            }

            if (invoke.Timeout is not null || invoke.OnTimeout.Count > 0)
            {
                problems.Add(
                    $"state '{node.Path}' gives its invoke '{invoke.Id}' a timeout. A call completes inside the " +
                    "step that starts it, so the timeout cannot fire. Remove it, and bound the call in its code.");
            }

            if (invoke.OnDone.Count > 0 && invoke.OnDone[^1].Guard is not null)
            {
                problems.Add(
                    $"state '{node.Path}' gives its invoke '{invoke.Id}' onDone transitions that all have guards. " +
                    "A result that no guard accepts would leave the machine in the state with nothing to move it " +
                    "on. End the onDone list with a transition that has no guard.");
            }

            if (invoke.OnSnapshot.Count > 0)
            {
                problems.Add(
                    $"state '{node.Path}' observes snapshots of its invoke '{invoke.Id}' (onSnapshot). A call has " +
                    "no snapshots. Remove onSnapshot.");
            }
        }

        foreach (StateNode<TContext> child in node.Children)
        {
            Walk(child, problems);
        }
    }
}
