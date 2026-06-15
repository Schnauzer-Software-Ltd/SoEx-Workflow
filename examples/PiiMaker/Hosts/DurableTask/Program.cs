using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Worker;
using Microsoft.DurableTask.Worker.AzureManaged;
using Microsoft.DurableTask.Client.AzureManaged;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiiMaker.Hosting;
using PiiMaker.Manager.Membership.Interface;
using PiiMaker.Manager.Membership.Service;
using SoEx.Workflow;
using SoEx.Workflow.DurableTask;
using SoEx.Workflow.InMemory;

// =============================================================================================
// Example host: the modern Durable Task SDK against a Durable Task Scheduler (DTS emulator on
// localhost:8080 in dev) — as a web CONTROL PANEL. The worker stays up for the whole run and the scheduler
// holds the durable state; a person drives the flows from the browser. One task hub, both consumption
// models coexisting by orchestration name:
//   A Onboarding   — NATIVE consumer orchestration (governed step activities + wait-for-accept)
//   B Subscription — PORTABLE driver continue-as-new renewal
//   C Offboarding  — NATIVE consumer orchestration fanning out governed revocations in parallel
//   D Erasure      — "forget subject" sweep (governance layer)
// Requires a Durable Task Scheduler on localhost:8080 (the DTS emulator, Docker). Only start this host when
// that scheduler is up.
// Run: dotnet run --project examples/PiiMaker/Hosts/DurableTask -- [port]  (default 5003), then open http://localhost:5003
// =============================================================================================

const string conn = "Endpoint=http://localhost:8080;Authentication=None;TaskHub=default";
const string subSystem = "membership";
int port = args is [var p, ..] && int.TryParse(p, out int n) ? n : 5003;

if (!SchedulerReachable())
{
    Console.WriteLine("✗ cannot reach a Durable Task Scheduler at localhost:8080; start the DTS emulator " +
                      "(docker run -p 8080:8080 mcr.microsoft.com/dts/dts-emulator) and retry.");
    return;
}

// The Workflow utility owns the durable governance stores; read them back to wire the governed step + termination.
MembershipSystem.Composition system = MembershipSystem.Compose(subSystem, MembershipPolicy.Default);
IInstanceKeyStore keys = system.Keys;
ISubjectIndex index = system.Index;
IIdempotencyStore idempotency = system.Idempotency;
var termination = new GovernedTermination(system.Erasure, keys, index, system.HeldLog);
GovernedStep<IMembershipManager> StepFor(string op) => new(system.Endpoint, system.Serializer, idempotency, keys, index, op);

// Each flow binds the operation it drives. Renew is the DI-resolved step the PORTABLE driver uses; the
// NATIVE flows (onboard/offboard) reach their steps through closures, so all three coexist on one host.
var onboardStep = StepFor(nameof(IMembershipManager.OnboardStep));
var renewStep = StepFor(nameof(IMembershipManager.Renew));
var offboardStep = StepFor(nameof(IMembershipManager.OffboardStep));

HostApplicationBuilder dtBuilder = Host.CreateApplicationBuilder();
dtBuilder.Logging.ClearProviders();
dtBuilder.Services.AddSingleton<IGovernedStep>(renewStep);   // the portable flow's step (flow B)
dtBuilder.Services.AddSingleton(termination);                    // shared termination for portable + native

dtBuilder.Services.AddDurableTaskWorker(worker =>
{
    worker.AddTasks(tasks =>
    {
        // B — the library's portable flow + its step/termination activities (resolve from DI)
        tasks.AddOrchestrator<WorkflowOrchestration>();
        tasks.AddActivity<StepActivity>();
        tasks.AddActivity<TerminateActivity>();

        // A + C — consumer-authored native orchestrations; the base termination hook resolves from DI
        tasks.AddOrchestrator<NativeOnboardOrchestration>();
        tasks.AddOrchestrator<NativeOffboardOrchestration>();
        tasks.AddActivity<GovernedTerminationActivity>();

        // A's governed steps: each kind is a named activity calling the governed OnboardStep (closure over
        // onboardStep). The seed rides sealed; the subject is recovered through the framework in the activity.
        StepReceipt Onboard(string kind, SealedStep c) =>
            MembershipNative.RunOnboardStep(onboardStep, c.InstanceId, c.Seq, kind, c.Seed).GetAwaiter().GetResult();
        tasks.AddActivityFunc<SealedStep, StepReceipt>("Lookup", (_, c) => Onboard("lookup", c));
        tasks.AddActivityFunc<SealedStep, StepReceipt>("Create", (_, c) => Onboard("create", c));
        tasks.AddActivityFunc<SealedStep, StepReceipt>("Reserve", (_, c) => Onboard("reserve", c));
        tasks.AddActivityFunc<SealedStep, StepReceipt>("Invite", (_, c) => Onboard("invite", c));
        tasks.AddActivityFunc<SealedStep, StepReceipt>("Assign", (_, c) => Onboard("assign", c));

        // C's governed revocation (closure over offboardStep); the fan-out calls it once per system.
        tasks.AddActivityFunc<RevokeInput, StepReceipt>("Revoke", (_, c) =>
            MembershipNative.RunOffboardStep(offboardStep, c.InstanceId, c.Seq, c.System, c.Seed).GetAwaiter().GetResult());
    });
    worker.UseDurableTaskScheduler(conn);
});
dtBuilder.Services.AddDurableTaskClient(client => client.UseDurableTaskScheduler(conn));

using IHost dtHost = dtBuilder.Build();
await dtHost.StartAsync();
DurableTaskClient client = dtHost.Services.GetRequiredService<DurableTaskClient>();

// Wire the workflow seam. Onboarding + offboarding are NATIVE here (their gateways target the consumer-
// authored orchestrations, an input factory adapting the sealed seed to each input shape); renewal is
// PORTABLE (the default gateway targets the library driver).
system.Seam.Connect("onboard",
    new DurableTaskWorkflowGateway(client, nameof(NativeOnboardOrchestration), seed => new NativeInput(seed, 120)),
    new WorkflowSealer(keys, system.Serializer, nameof(IMembershipManager.OnboardStep)), system.Serializer);
system.Seam.Connect("renew",
    new DurableTaskWorkflowGateway(client),
    new WorkflowSealer(keys, system.Serializer, nameof(IMembershipManager.Renew)), system.Serializer);
system.Seam.Connect("offboard",
    new DurableTaskWorkflowGateway(client, nameof(NativeOffboardOrchestration), seed => seed),
    new WorkflowSealer(keys, system.Serializer, nameof(IMembershipManager.OffboardStep)), system.Serializer);

WebApplicationBuilder webBuilder = MembershipWebHost.Create(port);
var capabilities = new MembershipWebHost.Capabilities(
    Runtime: "DurableTask", Onboarding: true, Renewal: true, Offboarding: true, Restart: false,
    Dashboard: MembershipWebHost.DashboardFromEnv());
WebApplication app = MembershipWebHost.Build(webBuilder, system, capabilities);

Console.WriteLine($"PiiMaker DurableTask control panel → http://localhost:{port}  (scheduler localhost:8080)");
await app.RunAsync();   // dtHost (the worker) is disposed/stopped when this scope ends

static bool SchedulerReachable()
{
    try
    {
        using var tcp = new TcpClient();
        return tcp.ConnectAsync("127.0.0.1", 8080).Wait(TimeSpan.FromSeconds(2)) && tcp.Connected;
    }
    catch (SocketException)
    {
        return false;
    }
}

// ---- the consumer's native flows (A, C) authored on the Durable Task SDK ----------------------

// The activity input for a governed native step: the opaque sealed seed + a PII-free sequence (and, for
// the fan-out, the target system). Never a plaintext DTO.
public sealed record SealedStep(byte[] Seed, string InstanceId, long Seq);
public sealed record RevokeInput(byte[] Seed, string InstanceId, long Seq, string System);
public sealed record NativeInput(byte[] Seed, int TimeoutSeconds);

// A — onboarding: a consumer-authored sequence of governed steps with a wait-for-accept (timeout falls
// through to a no-op completion — the happy path raises the event). The base orchestrator shreds at the end.
public sealed class NativeOnboardOrchestration : GovernedTaskOrchestrator<NativeInput, string>
{
    protected override async Task<string> Flow(TaskOrchestrationContext context, NativeInput input)
    {
        var retry = new TaskOptions(new TaskRetryOptions(new RetryPolicy(5, TimeSpan.FromMilliseconds(50))));
        string id = context.InstanceId;

        await context.CallActivityAsync<StepReceipt>("Lookup", new SealedStep(input.Seed, id, 0));
        await context.CallActivityAsync<StepReceipt>("Create", new SealedStep(input.Seed, id, 1), retry);
        await context.CallActivityAsync<StepReceipt>("Reserve", new SealedStep(input.Seed, id, 2));
        await context.CallActivityAsync<StepReceipt>("Invite", new SealedStep(input.Seed, id, 3));

        try
        {
            // byte[] payload so a gateway's bare raise (empty payload) resumes the wait
            await context.WaitForExternalEvent<byte[]>("invite-accepted", TimeSpan.FromSeconds(input.TimeoutSeconds));
        }
        catch (TaskCanceledException)
        {
            return "invite-timed-out";   // the durable timer won the race
        }

        await context.CallActivityAsync<StepReceipt>("Assign", new SealedStep(input.Seed, id, 4));
        return "assigned";
    }
}

// C — offboarding: revoke access across every downstream system in parallel; the subject rides sealed and
// is recovered per revocation. The base orchestrator's termination hook shreds when the fan-out completes.
public sealed class NativeOffboardOrchestration : GovernedTaskOrchestrator<byte[], string>
{
    protected override async Task<string> Flow(TaskOrchestrationContext context, byte[] seed)
    {
        string id = context.InstanceId;
        string[] systems = ["mail", "vpn", "billing-portal", "wiki"];

        var revocations = new List<Task>();
        for (int i = 0; i < systems.Length; i++)
        {
            revocations.Add(context.CallActivityAsync<StepReceipt>("Revoke", new RevokeInput(seed, id, i, systems[i])));
        }

        await Task.WhenAll(revocations);
        return "offboarded";
    }
}
