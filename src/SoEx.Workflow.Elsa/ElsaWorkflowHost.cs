using Elsa.Extensions;
using Elsa.Features.Services;
using Microsoft.Extensions.DependencyInjection;
using SoEx.Workflow;

namespace SoEx.Workflow.Elsa;

/// <summary>
/// Durable host — composes Elsa 3 with the consumer's persistence provider and workflow definitions, plus the
/// governed step and termination, into a <see cref="ServiceProvider"/> whose workflow state survives a host
/// restart. Persistence is the consumer's choice (so the adapter stays free of any EF-Core/store dependency):
/// supply it together with your workflow(s) in <paramref name="configureElsa"/>, e.g.
/// <c>elsa =&gt; { elsa.AddWorkflow&lt;MyFlow&gt;(); elsa.UseWorkflowManagement(m =&gt; m.UseEntityFrameworkCore(...));
/// elsa.UseWorkflowRuntime(r =&gt; r.UseEntityFrameworkCore(...)); }</c>. The governed step is registered both
/// as its concrete <c>GovernedStep&lt;I&gt;</c> (for native flows) and as <see cref="IGovernedStep"/> (for the
/// portable driver activity); the termination is registered too. For Tier-1 (in-memory, no persistence) use
/// <see cref="ElsaTestWorkflowHost"/>. Use this or the native flow, never both for one instance.
/// </summary>
public static class ElsaWorkflowHost
{
    /// <param name="step">The governed step (bound to its operation); registered as its concrete type and as <see cref="IGovernedStep"/>.</param>
    /// <param name="terminal">The governed termination (crypto-shred at the end of the flow).</param>
    /// <param name="configureElsa">Adds the workflow definition(s) and the persistence provider.</param>
    /// <param name="configureServices">Optional: register any extra singletons rehydrated activities resolve (e.g. the key store / subject index).</param>
    public static ServiceProvider BuildDurable(
        IGovernedStep step,
        GovernedTermination termination,
        Action<IModule> configureElsa,
        Action<IServiceCollection>? configureServices = null)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(termination);
        ArgumentNullException.ThrowIfNull(configureElsa);

        var services = new ServiceCollection();
        services.AddSingleton(step.GetType(), step); // resolvable as the concrete GovernedStep<I> (native flows)
        services.AddSingleton(step);                 // resolvable as IGovernedStep (portable driver activity)
        services.AddSingleton(termination);
        configureServices?.Invoke(services);
        services.AddElsa(configureElsa);
        return services.BuildServiceProvider();
    }
}
