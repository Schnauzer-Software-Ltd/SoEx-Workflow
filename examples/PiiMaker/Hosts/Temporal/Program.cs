using Microsoft.AspNetCore.Builder;
using PiiMaker.Hosting;
using PiiMaker.Manager.Membership.Interface;
using PiiMaker.Manager.Membership.Service;
using SoEx.Workflow;
using SoEx.Workflow.InMemory;
using SoEx.Workflow.Temporal;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Worker;
using Temporalio.Worker.Interceptors;
using Temporalio.Workflows;
using Wf = Temporalio.Workflows.Workflow;

// =============================================================================================
// Example host: a REAL, permanent Temporal server (localhost:7233) — as a web CONTROL PANEL. The workers
// stay up for the whole run and the server holds the durable state; a person drives the flows from the
// browser. All four flows are reachable:
//   A Onboarding   — portable flow (Temporal signals / timeout / termination shred)
//   B Subscription — portable flow continue-as-new renewal
//   C Offboarding  — NATIVE [Workflow] fanning out governed revocations in parallel
//   D Erasure      — "forget subject" sweep (governance layer)
// Requires a Temporal server on localhost:7233 (Docker). Only start this host when that server is up.
// Run: dotnet run --project examples/PiiMaker/Hosts/Temporal -- [port]  (default 5002), then open http://localhost:5002
// =============================================================================================

const string server = "localhost:7233";
const string onboardQ = "pii-onboard", renewQ = "pii-renew", offboardQ = "pii-offboard";
int port = args is [var p, ..] && int.TryParse(p, out int n) ? n : 5002;

// The Workflow utility owns the durable governance stores; read them back to wire the governed step + termination.
MembershipSystem.Composition system = MembershipSystem.Compose("membership", MembershipPolicy.Default);
IInstanceKeyStore keys = system.Keys;
ISubjectIndex index = system.Index;
IIdempotencyStore idempotency = system.Idempotency;
var termination = new GovernedTermination(system.Erasure, keys, index, system.HeldLog);
GovernedStep<IMembershipManager> StepFor(string op) => new(system.Endpoint, system.Serializer, idempotency, keys, index, op);

var onboardStep = StepFor(nameof(IMembershipManager.Onboard));
var renewStep = StepFor(nameof(IMembershipManager.Renew));
var offboardStep = StepFor(nameof(IMembershipManager.OffboardStep));

TemporalClient client;
try
{
    client = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions(server) { Namespace = "default" });
}
catch (Exception ex)
{
    Console.WriteLine($"✗ cannot reach a Temporal server at {server} ({ex.GetType().Name}); start one (Docker) and retry.");
    return;
}

// One worker per task queue — portable onboarding, portable renewal, native offboarding — kept alive for the
// whole process (cancelled on shutdown), so the server can resume flows the UI drives.
using var onboardWorker = TemporalWorkflowHost.BuildWorker(client, onboardQ, onboardStep, termination);
using var renewWorker = TemporalWorkflowHost.BuildWorker(client, renewQ, renewStep, termination);

var offboardOptions = new TemporalWorkerOptions(offboardQ)
    .AddAllActivities(new GovernedOffboard(offboardStep))
    .AddAllActivities(new GovernedTerminationActivities(termination))
    .AddWorkflow<NativeOffboardWorkflow>();
offboardOptions.Interceptors = new IWorkerInterceptor[] { new GovernedTerminationInterceptor() };
using var offboardWorker = new TemporalWorker(client, offboardOptions);

// Wire the workflow seam: portable onboarding + renewal via the generic gateway, native offboarding via a
// gateway that starts the consumer-authored fan-out workflow. This is the only runtime-specific wiring.
system.Seam.Connect("onboard", new TemporalWorkflowGateway(client, onboardQ),
    new WorkflowSealer(keys, system.Serializer, nameof(IMembershipManager.Onboard)), system.Serializer);
system.Seam.Connect("renew", new TemporalWorkflowGateway(client, renewQ),
    new WorkflowSealer(keys, system.Serializer, nameof(IMembershipManager.Renew)), system.Serializer);
system.Seam.Connect("offboard", new NativeOffboardGateway(client, offboardQ),
    new WorkflowSealer(keys, system.Serializer, nameof(IMembershipManager.OffboardStep)), system.Serializer);

using var cts = new CancellationTokenSource();
Task workers = Task.WhenAll(
    onboardWorker.ExecuteAsync(cts.Token),
    renewWorker.ExecuteAsync(cts.Token),
    offboardWorker.ExecuteAsync(cts.Token));

WebApplicationBuilder builder = MembershipWebHost.Create(port);
var capabilities = new MembershipWebHost.Capabilities(
    Runtime: "Temporal", Onboarding: true, Renewal: true, Offboarding: true, Restart: false,
    Dashboard: MembershipWebHost.DashboardFromEnv());
WebApplication app = MembershipWebHost.Build(builder, system, capabilities);
app.Lifetime.ApplicationStopping.Register(() => cts.Cancel());

Console.WriteLine($"PiiMaker Temporal control panel → http://localhost:{port}  (server {server})");
await app.RunAsync();
try { await workers; } catch { /* workers cancelled on shutdown */ }

// ---- the consumer's native offboarding flow (C): a Temporal [Workflow] that fans out revocations -------

/// <summary>The <see cref="IWorkflowGateway"/> that starts the consumer-authored native offboarding
/// workflow (the generic gateway would start the portable flow instead). Offboarding fans out and
/// completes on its own, so it has no continuation events.</summary>
sealed class NativeOffboardGateway(ITemporalClient client, string taskQueue) : IWorkflowGateway
{
    public async Task StartAsync(string instanceId, byte[] sealedSeed)
    {
        byte[] seed = sealedSeed;   // a local for the expression-tree lambda
        await client.StartWorkflowAsync((NativeOffboardWorkflow wf) => wf.Run(seed),
            new WorkflowOptions(id: instanceId, taskQueue: taskQueue));
    }

    public Task RaiseEventAsync(string instanceId, string eventName, byte[]? sealedPayload = null, string? raiseId = null) =>
        throw new NotSupportedException("the offboarding fan-out completes on its own; it has no continuation events");
}

// One governed revocation per downstream system; the subject is recovered through the framework.
sealed class GovernedOffboard(GovernedStep<IMembershipManager> step)
{
    [Activity]
    public Task<StepReceipt> Revoke(byte[] seed, string system, long seq) =>
        MembershipNative.RunOffboardStep(step, ActivityExecutionContext.Current.Info.WorkflowId!, seq, system, seed);
}

[Workflow]
public class NativeOffboardWorkflow
{
    [WorkflowRun]
    public async Task<string> Run(byte[] seed)
    {
        var options = new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) };
        string[] systems = ["mail", "vpn", "billing-portal", "wiki"];

        // fan out: revoke access across every system in parallel (the GovernedTerminationInterceptor runs the
        // termination shred when the workflow completes)
        var revocations = new List<Task>();
        for (int i = 0; i < systems.Length; i++)
        {
            string system = systems[i];
            long seq = i;
            revocations.Add(Wf.ExecuteActivityAsync((GovernedOffboard a) => a.Revoke(seed, system, seq), options));
        }

        await Task.WhenAll(revocations);
        return "offboarded";
    }
}
