using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SoEx.Abstractions;
using SoEx.Context;
using Microsoft.Extensions.Logging;
using SoEx;
using SoEx.Hosting;
using SoEx.Hosting.Default;
using SoEx.Transport.Workflow;
using SoEx.Workflow;
using SoEx.Workflow.Runtime.InMemory;
using SoEx.Workflow.Statecharts;
using StatechartDemo;
using XState;
using XState.Json;

// =============================================================================================
// Example: a STATECHART as a governed workflow.
//
// The chart is drawn in a statechart tool, exported as XState v6 JSON, and shipped as an embedded
// resource. It is loaded ONCE, here at start. It never crosses the wire — only the machine's snapshot
// does, sealed under the instance key like any other step payload, and fed back into the chart this
// process already holds.
//
// Nothing about the flow is a new model: the chart runs as an ordinary step component, so it inherits
// the sealed journal, the crypto-shred at termination, the PII guards, idempotency and subject
// enrollment unchanged — and the same component would run on Durable Task, Temporal, Elsa or Restate
// without a line of it changing.
//
// Run:  dotnet run --project examples/Statechart
// =============================================================================================

Console.OutputEncoding = Encoding.UTF8;

// ---- 1. Load the chart, once, at start ------------------------------------------------------

string chartJson = ReadEmbedded("approval.chart.json");

// The chart names its actions; the code behind those names is supplied here. A name with no
// implementation is refused at import — all of them at once — so a chart and its host cannot drift
// apart silently. The drawing owns the shape of the flow; this owns what the boxes do.
var notified = new List<string>();
StateMachine<JsonElement> machine = MachineConfig.FromJson(
    chartJson,
    new MachineImplementations<JsonElement>()
        .Action("notifyApproved", args => notified.Add($"notified: approved by {Who(args.Event)}"))
        .Action("notifyRejected", args => notified.Add($"notified: rejected by {Who(args.Event)}"))
        .Action("notifyEscalated", _ => notified.Add("escalated: nobody approved before the chart's timer fired")));

Console.WriteLine($"chart    {machine.Id} v{machine.Version}  (embedded, loaded once at start)");

var chart = new StatechartStep(machine, new StatechartOptions
{
    // The events an outside caller may raise at a parked machine, in declared order. Declared rather than
    // derived: every one of these names is journaled in clear as its runtime's delivery key, so which names
    // a flow exposes is a decision to make on purpose.
    ResumableEvents = ["approve", "reject"],
    // A snapshot crosses the journal as JSON, so say how to read the context back.
    ContextConverter = StatechartOptions.JsonContext<JsonElement>(),
});

// ---- 2. Compose the governed step ----------------------------------------------------------
// The real SoEx composition: the component is hosted on a WorkflowBinding, the in-proc listener
// registry hands back its dispatch endpoint, and GovernedStep wraps it with governance. Swap the
// in-memory stores for OpenBao/RavenDB and this is a production wiring.

var keys = new InMemoryInstanceKeyStore();
var index = new InMemorySubjectIndex();
var listeners = new WorkflowListeners();
const string subSystem = "approvals";

IServiceCollection componentServices = new ServiceCollection()
    .AddSingleton(listeners)
    // What the hosted component is constructed FROM. SoEx builds the entrypoint per invocation inside the
    // component's own scope, so the chart is what has to be registered — the chart being an immutable value
    // is exactly why one registration serves every step and every instance.
    .AddSingleton(chart)
    .AddSingleton<IContextFlowPolicy, SubjectContextFlowPolicy>();

var topology = new SoEx.Topology.System
{
    Defaults = new DefaultPipeline(),
    Clients = [],
    SubSystems =
    [
        new SoEx.Topology.SubSystem
        {
            Name = subSystem,
            Components = [],
            EntryPoint = new SoEx.Topology.Host
            {
                Implementation = typeof(ApprovalFlow),
                Endpoints = [new WorkflowBinding<IApprovalFlow>(subSystem)],
                Proxies = [],
                ServiceCollection = componentServices,
            },
        },
    ],
};

HostApplicationBuilder builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.SoEx(topology);
Microsoft.Extensions.Hosting.IHost host = builder.Build();
host.Start();

IWorkflowDispatch endpoint = listeners.ForAddress(
    new WorkflowBinding<IApprovalFlow>(subSystem).Transport.Address.Uri);
var serializer = host.Services.GetRequiredService<IMessageSerializer>();

var step = new GovernedStep<IApprovalFlow>(endpoint, serializer, idempotency: null, keys, index);
// The erasure contract the termination calls. It is a second instance of the same component, which is
// harmless here precisely because the erasure hooks hold no per-instance state: everything they could need
// is in the sealed journal, and this flow keeps nothing outside it. A component that DID extract data in
// OnRetaining would resolve its contract from the component scope instead.
var termination = new GovernedTermination(new ApprovalFlow(chart), keys, index);

// ---- 3. Run it: approved by a raise carrying who approved ------------------------------------

await RunAsync("approved by a raise that says who", raise: async (runtime, instanceId) =>
{
    await Task.Delay(200);   // let it reach the parked wait
    // The raiser knows who approved; the chart decided, at wait time, that "approve" is what moves it.
    await runtime.RaiseEventAsync(instanceId, "approve",
        step.SealEventData(instanceId, new MachineEventData("\"ana\"")));
});

// ---- 4. Run it again, and let the chart's own `after` timer escalate --------------------------

await RunAsync("escalated by the chart's own timer, then approved", raise: async (runtime, instanceId) =>
{
    await Task.Delay(200);   // let it reach the parked wait

    // Nobody approves in time. The chart's `after` became the wait's durable timeout, so the flow escalates
    // on its own — no scheduler here, just the machine's declared delay routed onto the runtime's timer.
    //
    // InProc's durable timer is VIRTUAL: it fires when the run's clock is advanced, which is what makes a
    // 72-hour flow testable in milliseconds. On Durable Task, Temporal, Elsa or Restate the same wait is a
    // real durable timer and this line is simply absent.
    runtime.Advance(TimeSpan.FromSeconds(3));
    await Task.Delay(200);   // let the escalation step run and re-park

    await runtime.RaiseEventAsync(instanceId, "approve",
        step.SealEventData(instanceId, new MachineEventData("\"the duty manager\"")));
});

host.Dispose();
return;

async Task RunAsync(string label, Func<InMemoryWorkflowRuntime, string, Task> raise)
{
    notified.Clear();
    string instanceId = "expense-" + Guid.NewGuid().ToString("N")[..8];

    // The subject rides the ambient, sealed with every step; the instance id stays PII-free because it is
    // journaled in clear on every engine.
    byte[] seed = step.SealStep(instanceId, chart.Seed(),
        WorkflowEnvelope.AmbientFor(serializer, SubjectContext.Managed("ana@example.com")));

    var runtime = new InMemoryWorkflowRuntime(instanceId);
    Task<byte[]> run = new WorkflowDriver<IApprovalFlow>(runtime, step, termination).RunAsync(seed);

    Console.WriteLine($"\n── {label}");
    Console.WriteLine($"   instance {instanceId}   key live? {keys.Has(instanceId)}");

    await raise(runtime, instanceId);
    byte[] result = await run;

    // A chart has no CLR output type, so a statechart-backed flow completes with its machine's output
    // as JSON and the consumer reads what it needs out of it.
    Console.WriteLine($"   result   {serializer.Deserialize<string>(result)}");
    foreach (string line in notified)
    {
        Console.WriteLine($"   {line}");
    }

    // The termination destroyed the per-instance key, so every payload the journal still holds for this
    // instance is now unrecoverable — that is the crypto-shred, and the chart got it for free.
    Console.WriteLine($"   key live after completion? {keys.Has(instanceId)}  (false = journal crypto-shredded)");
}

static string Who(MachineEvent raised) =>
    raised is NamedEvent { Data: { } data } ? data.ToString()! : "someone unnamed";

static string ReadEmbedded(string fileName)
{
    Assembly assembly = Assembly.GetExecutingAssembly();
    string name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(fileName, StringComparison.Ordinal));
    using Stream stream = assembly.GetManifestResourceStream(name)!;
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
}
