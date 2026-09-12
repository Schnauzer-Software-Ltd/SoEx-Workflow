using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SoEx;
using SoEx.Abstractions;
using SoEx.Context;
using SoEx.Hosting;
using SoEx.Hosting.Default;
using SoEx.Transport.Workflow;
using SoEx.Workflow;
using SoEx.Workflow.Runtime.InMemory;
using SoEx.Workflow.Statecharts;
using StatechartDemo.Access.Notification.Interface;
using StatechartDemo.Access.Notification.Service;
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
        // Loaded once, at start. The chart never crosses the wire; only a claim's snapshot does.
        INotificationAccess notifications = new NotificationAccess();
        StatechartStep process = ExpenseApprovalChart.Load(notifications);
        Console.WriteLine($"process  {ExpenseApprovalChart.ProcessId}  (embedded with the manager, loaded once)");

        // ---- the governed step: SoEx hosts the manager, governance wraps it --------------------------
        var keys = new InMemoryInstanceKeyStore();
        var index = new InMemorySubjectIndex();
        var listeners = new WorkflowListeners();

        IServiceCollection managerServices = new ServiceCollection()
            .AddSingleton(listeners)
            .AddSingleton(process)
            .AddSingleton(notifications)
            .AddSingleton<IContextFlowPolicy, SubjectContextFlowPolicy>();

        var topology = new SoEx.Topology.System
        {
            Defaults = new DefaultPipeline(),
            Clients = [],
            SubSystems =
            [
                new SoEx.Topology.SubSystem
                {
                    Name = SubSystem,
                    Components = [],
                    EntryPoint = new SoEx.Topology.Host
                    {
                        Implementation = typeof(ExpenseManager),
                        Endpoints = [new WorkflowBinding<IExpenseManager>(SubSystem)],
                        Proxies = [],
                        ServiceCollection = managerServices,
                    },
                },
            ],
        };

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.SoEx(topology);
        using IHost host = builder.Build();
        host.Start();

        IWorkflowDispatch endpoint = listeners.ForAddress(
            new WorkflowBinding<IExpenseManager>(SubSystem).Transport.Address.Uri);
        var serializer = host.Services.GetRequiredService<IMessageSerializer>();

        var step = new GovernedStep<IExpenseManager>(endpoint, serializer, idempotency: null, keys, index);
        var termination = new GovernedTermination(new ExpenseManager(process), keys, index);

        // ---- two claims ------------------------------------------------------------------------------

        await RunClaim("approved by a manager who is named in the raise", approveAfterEscalation: false);
        await RunClaim("nobody approves, the process escalates itself, then approved", approveAfterEscalation: true);

        async Task RunClaim(string label, bool approveAfterEscalation)
        {
            int alreadySent = notifications.Sent.Count;
            string claimId = "claim-" + Guid.NewGuid().ToString("N")[..8];

            // The claimant is the subject: it rides the ambient, sealed with every step. The instance id is
            // journaled in clear on every engine, so it stays PII-free.
            // The chart's context IS its JSON, so the claim starts as a JSON document rather than a CLR object.
            byte[] seed = step.SealStep(claimId, process.Seed(JsonSerializer.SerializeToElement(new { claimId })),
                WorkflowEnvelope.AmbientFor(serializer, SubjectContext.Managed("ana@example.com")));

            var runtime = new InMemoryWorkflowRuntime(claimId);
            Task<byte[]> claim = new WorkflowDriver<IExpenseManager>(runtime, step, termination).RunAsync(seed);

            Console.WriteLine($"\n── {label}");
            Console.WriteLine($"   {claimId}   key live? {keys.Has(claimId)}");

            await Task.Delay(200);   // let the claim reach its parked wait

            if (approveAfterEscalation)
            {
                // Nobody approves in time, so the process escalates on its own — the chart's own delayed
                // transition, routed onto the runtime's durable timer. InProc's timer is virtual, which is what
                // makes a 72-hour process testable in milliseconds; on a durable engine this line is absent.
                runtime.Advance(TimeSpan.FromSeconds(3));
                await Task.Delay(200);
            }

            // Who approved is knowable only to the approver, so it travels with the event rather than as a step
            // the caller would have to author.
            await runtime.RaiseEventAsync(claimId, "approve",
                step.SealEventData(claimId, new MachineEventData(
                    approveAfterEscalation ? "\"the duty manager\"" : "\"ana\"")));

            byte[] result = await claim;

            // A chart has no CLR output type, so the process completes with its own output as JSON.
            Console.WriteLine($"   outcome  {serializer.Deserialize<string>(result)}");
            foreach (string sent in notifications.Sent.Skip(alreadySent))
            {
                Console.WriteLine($"   notified {sent}");
            }

            // The termination destroyed the per-claim key, so everything the journal still holds for this claim
            // is unrecoverable. The process got that by being an ordinary governed manager.
            Console.WriteLine($"   key live after completion? {keys.Has(claimId)}  (false = journal crypto-shredded)");
        }
    }
}
