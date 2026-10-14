using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SoEx;
using SoEx.Abstractions;
using SoEx.Context;
using SoEx.Hosting;
using SoEx.Hosting.Default;
using SoEx.Transport.InProc;
using SoEx.Transport.Workflow;
using SoEx.Workflow;
using SoEx.Workflow.Runtime.InMemory;
using SoEx.Workflow.Statecharts;
using StatechartDemo.Access.Notification.Interface;
using StatechartDemo.Access.Notification.Service;
using StatechartDemo.Engine.Budget.Interface;
using StatechartDemo.Engine.Budget.Service;
using StatechartDemo.Manager.Expense.Interface;
using StatechartDemo.Manager.Expense.Service;

namespace StatechartDemo.Hosts.InProc;

/// <summary>
/// The host: framework wiring and nothing else. Every decision this program makes is about plumbing — which
/// runtime, which stores, which transport. What the flow DOES lives entirely under <c>Component/</c>: the
/// process is the manager's chart, and the steps it runs are the manager's calls.
/// <para>
/// Run: <c>dotnet run --project examples/Statechart</c>. No backend needed.
/// </para>
/// </summary>
public static class Program
{
    private const string SubSystem = "expenses";

    public static async Task Main()
    {
        // ---- the business component, and the process it owns ------------------------------------------
        // Loaded once, at start. The chart never crosses the wire; only a claim's snapshot does. It holds no
        // component: SoEx resolves the manager and the components it depends on per call, and the manager
        // hands them to the process on each step.
        StatechartStep process = ExpenseApprovalChart.Load();
        Console.WriteLine($"process  {ExpenseApprovalChart.ProcessId}  (embedded with the manager, loaded once)");

        // ---- the governed step: SoEx hosts the manager, governance wraps it --------------------------
        var keys = new InMemoryInstanceKeyStore();
        var index = new InMemorySubjectIndex();
        var listeners = new WorkflowListeners();

        IServiceCollection managerServices = new ServiceCollection()
            .AddSingleton(listeners)
            // The process is a singleton because a machine is an immutable value. The manager that uses it, and
            // the components the manager depends on, are constructed per call.
            .AddSingleton(process)
            .AddSingleton<IContextFlowPolicy, SubjectContextFlowPolicy>();

        // The engine and the access are components of their own. The manager reaches each one through a proxy,
        // and SoEx resolves them per call.
        IServiceCollection budgetServices = new ServiceCollection().AddSingleton<IContextFlowPolicy, SubjectContextFlowPolicy>();
        IServiceCollection notificationServices = new ServiceCollection().AddSingleton<IContextFlowPolicy, SubjectContextFlowPolicy>();

        static SoEx.Topology.Host Component<TContract, TImpl>(IServiceCollection services) => new()
        {
            Implementation = typeof(TImpl),
            Endpoints = [new InProcBinding<TContract>(SubSystem)],
            Proxies = [],
            ServiceCollection = services,
        };

        static SoEx.Topology.Client Proxy<T>() where T : class =>
            new SoEx.Topology.Client<T> { SubSystem = SubSystem, Service = new InProcBinding<T>(SubSystem) };

        var topology = new SoEx.Topology.System
        {
            Defaults = new DefaultPipeline(),
            Clients = [],
            SubSystems =
            [
                new SoEx.Topology.SubSystem
                {
                    Name = SubSystem,
                    Components =
                    [
                        Component<IBudgetEngine, BudgetEngine>(budgetServices),
                        Component<INotificationAccess, NotificationAccess>(notificationServices),
                    ],
                    EntryPoint = new SoEx.Topology.Host
                    {
                        Implementation = typeof(ExpenseManager),
                        Endpoints = [new WorkflowBinding<IExpenseManager>(SubSystem)],
                        Proxies = [Proxy<IBudgetEngine>(), Proxy<INotificationAccess>()],
                        ServiceCollection = managerServices,
                    },
                },
            ],
        };

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();

        // One in-proc listener registry shared by the manager and every component, so each proxy reaches its
        // component through the in-proc transport.
        InProcExtensions.InProcClientWithSpan(builder.Services, managerServices, budgetServices, notificationServices);
        // The stock pipeline serializes with System.Text.Json, which binds values to their declared types. The
        // ambient SubjectContext entry and the steps the portable flow carries in WorkflowAction are declared `object`,
        // so the serializer is told about them here.
        builder.SoEx(topology, new KnownTypes([
            .. WorkflowKnownTypes.Framework,
            typeof(MachineStep),
            typeof(MachineEventData),
        ]));
        using IHost host = builder.Build();
        host.Start();

        IWorkflowDispatch endpoint = listeners.ForAddress(
            new WorkflowBinding<IExpenseManager>(SubSystem).Transport.Address.Uri);
        var serializer = host.Services.GetRequiredService<IMessageSerializer>();

        var step = new GovernedStep<IExpenseManager>(endpoint, serializer, idempotency: null, keys, index);
        // The termination calls only the manager's erasure hooks, which use none of its components.
        var termination = new GovernedTermination(
            new ExpenseManager(process, new BudgetEngine(), new NotificationAccess()), keys, index);

        // ---- four claims ------------------------------------------------------------------------------

        await RunClaim("a small claim: the budget review approves it, and nobody is asked", 80m, approver: null);
        await RunClaim("approved by a manager who is named in the raise", 450m, approver: "ana");
        await RunClaim("nobody approves, the process escalates itself, then approved", 450m, approver: "the duty manager",
            escalateFirst: true);
        await RunClaim("over budget: the budget review rejects it, and nobody is asked", 5000m, approver: null);

        async Task RunClaim(string label, decimal amount, string? approver, bool escalateFirst = false)
        {
            string claimId = "claim-" + Guid.NewGuid().ToString("N")[..8];

            // The claimant is the subject: it rides the ambient, sealed with every step. The instance id is
            // journaled in clear on every engine, so it stays PII-free.
            // The chart's context IS its JSON, so the claim starts as a JSON document rather than a CLR object.
            // The seed runs nothing: the budget review is the process's first work, and it runs in the first
            // governed step, under the step's seal and retry.
            byte[] seed = step.SealStep(claimId, process.Seed(JsonSerializer.SerializeToElement(new { claimId, amount })),
                WorkflowEnvelope.AmbientFor(serializer, SubjectContext.Managed("ana@example.com")));

            Console.WriteLine($"\n── {label}");
            Console.WriteLine($"   {claimId}   amount {amount}   key live? {keys.Has(claimId)}");

            var runtime = new InMemoryWorkflowRuntime(claimId);
            Task<byte[]> claim = new WorkflowDriver<IExpenseManager>(runtime, step, termination).RunAsync(seed);

            await Task.Delay(200);   // let the claim reach its parked wait, or finish

            if (escalateFirst)
            {
                // Nobody approves in time, so the process escalates on its own — the chart's own delayed
                // transition, routed onto the runtime's durable timer. InProc's timer is virtual, which is what
                // makes a 72-hour process testable in milliseconds; on a durable engine this line is absent.
                runtime.Advance(TimeSpan.FromSeconds(3));
                await Task.Delay(200);
            }

            if (approver is not null)
            {
                // Who approved is knowable only to the approver, so it travels with the event rather than as a
                // step the caller would have to author.
                await runtime.RaiseEventAsync(claimId, "approve",
                    step.SealEventData(claimId, new MachineEventData(JsonSerializer.Serialize(approver))));
            }

            byte[] result = await claim;

            // A chart has no CLR output type, so the process completes with its own output as JSON. The
            // notifications above this line were written by the component as each step ran.
            Console.WriteLine($"   outcome  {serializer.Deserialize<string>(result)}");

            // The termination destroyed the per-claim key, so everything the journal still holds for this claim
            // is unrecoverable. The process got that by being an ordinary governed manager.
            Console.WriteLine($"   key live after completion? {keys.Has(claimId)}  (false = journal crypto-shredded)");
        }
    }
}
