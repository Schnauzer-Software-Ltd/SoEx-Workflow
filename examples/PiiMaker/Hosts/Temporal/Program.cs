using Microsoft.AspNetCore.Builder;
using PiiMaker.Host.Temporal;
using PiiMaker.Hosting;
using PiiMaker.Manager.Membership.Interface;
using Native = PiiMaker.Manager.Membership.Interface.Native;
using Portable = PiiMaker.Manager.Membership.Interface.Portable;
using SoEx.Workflow;
using SoEx.Workflow.Runtime.InMemory;
using SoEx.Workflow.Runtime.Temporal;
using Temporalio.Client;
using Temporalio.Worker;
using Temporalio.Worker.Interceptors;

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

internal class Program
{
    static async Task Main(string[] args)
    {
        const string server = "localhost:7233";
        const string onboardQ = "pii-onboard", renewQ = "pii-renew", offboardQ = "pii-offboard";
        int port = args is [var p, ..] && int.TryParse(p, out int n) ? n : 5002;

        // The Workflow utility owns the durable governance stores; read them back to wire the governed step + termination.
        MembershipSystem.Composition system = MembershipSystem.Compose("membership", MembershipPolicy.Default);
        IInstanceKeyStore keys = system.Keys;
        ISubjectIndex index = system.Index;
        IIdempotencyStore idempotency = system.Idempotency;
        var termination = new GovernedTermination(system.Erasure, keys, index, system.HeldLog);
        // Onboarding + renewal drive the PORTABLE contract; offboarding's fan-out drives the NATIVE one. Each
        // governed step binds the seam (and endpoint) of the contract that owns its operation.
        GovernedStep<Portable.IMembershipManager> PortableStep(string op) => new(system.PortableEndpoint, system.Serializer, idempotency, keys, index, op);
        GovernedStep<Native.IMembershipManager> NativeStep(string op) => new(system.NativeEndpoint, system.Serializer, idempotency, keys, index, op);

        var onboardStep = PortableStep(nameof(Portable.IMembershipManager.Onboard));
        var renewStep = PortableStep(nameof(Portable.IMembershipManager.Renew));
        var offboardStep = NativeStep(nameof(Native.IMembershipManager.Offboard));

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
            new WorkflowSealer(keys, system.Serializer, nameof(Portable.IMembershipManager.Onboard)), system.Serializer);
        system.Seam.Connect("renew", new TemporalWorkflowGateway(client, renewQ),
            new WorkflowSealer(keys, system.Serializer, nameof(Portable.IMembershipManager.Renew)), system.Serializer);
        system.Seam.Connect("offboard", new NativeOffboardGateway(client, offboardQ),
            new WorkflowSealer(keys, system.Serializer, nameof(Native.IMembershipManager.Offboard)), system.Serializer);

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
    }
}
