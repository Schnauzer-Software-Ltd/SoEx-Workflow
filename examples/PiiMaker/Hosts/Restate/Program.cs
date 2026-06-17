using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using PiiMaker.Host.Restate;
using PiiMaker.Hosting;
using PiiMaker.Manager.Membership.Interface;
using Native = PiiMaker.Manager.Membership.Interface.Native;
using Portable = PiiMaker.Manager.Membership.Interface.Portable;
using SoEx.Workflow;
using SoEx.Workflow.Runtime.Restate;

// =============================================================================================
// Example host: Restate — the cross-language runtime — as a web CONTROL PANEL. Restate ships no .NET SDK, so
// the durable flow runs out-of-process in a compiled Restate sidecar hosted by restate-server, calling back to a
// thin .NET governed-step host over HTTP. This example uses its OWN Restate sidecar (examples/PiiMaker/Hosts/Restate/
// sidecar-rs), so it can author the consumer's NATIVE flows including offboarding's parallel fan-out.
//
// The panel drives all flows over the language boundary:
//   A Onboarding   — NATIVE `MembershipOnboard` Rust flow; .NET governs each step via /gov-step
//   B Subscription — PORTABLE generic `MembershipPortable` sidecar (continue-as-new renewal + dunning)
//   C Offboarding  — NATIVE `MembershipOffboard` Rust flow fanning out governed revocations in parallel
//   D Erasure      — "forget subject" sweep (governance layer, in-process)
// The native services call back to ONE long-lived /gov-step host (routed by instance-id prefix); the portable
// sidecar calls back to its own /step+/terminate host on a SEPARATE port (so the two callback hosts coexist for
// the whole run). The sidecar's MembershipPortable is pointed at that port via PORTABLE_STEP_URL.
//
// The sidecar build/spawn/stop + port helpers live in RestateSidecar; the /gov-step callback host in GovStepHost.
// Requires restate-server (ingress :8088, admin :9070) running (Docker). The Restate sidecar is built (cargo) and
// spawned by this host, then killed on exit. Only start this host when restate-server is up.
// Run: dotnet run --project examples/PiiMaker/Hosts/Restate -- [port]  (default 5004), then open http://localhost:5004
// =============================================================================================

internal class Program
{
    static async Task Main(string[] args)
    {
        const string ingress = "http://localhost:8088", admin = "http://localhost:9070";
        const string sidecarUri = "http://127.0.0.1:9081";   // example sidecar endpoint (the test sidecar uses :9080)
        const string stepUrl = "http://127.0.0.1:9091";     // native /gov-step callback host (test uses :9090)
        const string portableStepUrl = "http://127.0.0.1:9092";   // portable /step+/terminate callback host (renewal)
        const int sidecarPort = 9081;
        const string token = "pii-example-token";           // shared secret: Restate sidecar <-> .NET callback host
        const string subSystem = "membership";
        int port = args is [var p, ..] && int.TryParse(p, out int n) ? n : 5004;

        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        if (!RestateSidecar.Reachable("localhost", 8088) || !RestateSidecar.Reachable("localhost", 9070))
        {
            Console.WriteLine("✗ restate-server not reachable on :8088/:9070; start it (Docker) and retry.");
            return;
        }
        if (RestateSidecar.Reachable("127.0.0.1", sidecarPort))
        {
            Console.WriteLine($"✗ something is already listening on :{sidecarPort}; stop the other sidecar and retry.");
            return;
        }

        // Build (if needed) + spawn the example's own Restate sidecar, then point restate at it. It stays up the whole run.
        string sidecarDir = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "RestateSidecarRsDir").Value!;
        string sidecarBin = Path.Combine(sidecarDir, "target", "release", "piimaker-sidecar");
        if (!File.Exists(sidecarBin)) RestateSidecar.TryBuild(sidecarDir);
        if (!File.Exists(sidecarBin))
        {
            Console.WriteLine("✗ Restate sidecar binary missing and `cargo build --release` unavailable/failed; install cargo and retry.");
            return;
        }

        Process sidecar = RestateSidecar.Start(sidecarBin, sidecarDir);
        if (!RestateSidecar.WaitForPort(sidecarPort, TimeSpan.FromSeconds(20)))
        {
            Console.WriteLine($"✗ the Restate sidecar did not start listening on :{sidecarPort}.");
            RestateSidecar.Kill(sidecar);
            return;
        }
        HttpResponseMessage reg = await http.PostAsync($"{admin}/deployments", RestateSidecar.JsonBody(new { uri = sidecarUri, force = true }));
        if (!reg.IsSuccessStatusCode)
        {
            Console.WriteLine($"✗ restate could not discover the Restate sidecar ({(int)reg.StatusCode}).");
            RestateSidecar.Kill(sidecar);
            return;
        }

        // The membership system (manager + engine/access components). Governance is shared across the callback
        // host and the in-process erasure sweep, so the per-instance keys persist as the active operation changes.
        // The Workflow utility owns the durable governance stores; read them back to wire the governed step + termination.
        MembershipSystem.Composition system = MembershipSystem.Compose(subSystem, MembershipPolicy.Default);
        IInstanceKeyStore keys = system.Keys;
        ISubjectIndex index = system.Index;
        IIdempotencyStore idempotency = system.Idempotency;
        var termination = new GovernedTermination(system.Erasure, keys, index, system.HeldLog);
        // Onboarding + offboarding are NATIVE here; only renewal uses the PORTABLE flow. Each governed step
        // binds the seam (and endpoint) of the contract that owns its operation.
        GovernedStep<Native.IMembershipManager> NativeStep(string op) => new(system.NativeEndpoint, system.Serializer, idempotency, keys, index, op);
        GovernedStep<Portable.IMembershipManager> PortableStep(string op) => new(system.PortableEndpoint, system.Serializer, idempotency, keys, index, op);

        var onboardStep = NativeStep(nameof(Native.IMembershipManager.Onboard));
        var offboardStep = NativeStep(nameof(Native.IMembershipManager.Offboard));
        var renewStep = PortableStep(nameof(Portable.IMembershipManager.Renew));

        // One long-lived governed-step callback host: both native Rust flows call back here. The onboarding and
        // offboarding flows are told apart by the instance-id prefix the entry seam derived (onboard- / offboard-),
        // dispatching to the matching governed operation. Stays up for the whole process.
        WebApplication govHost = GovStepHost.Build(stepUrl, token, termination, async req =>
        {
            byte[] data = Convert.FromBase64String(req.Data);
            return req.InstanceId.StartsWith("offboard-", StringComparison.Ordinal)
                ? Results.Json(await MembershipNative.RunOffboardStep(offboardStep, req.InstanceId, req.Seq, req.Kind, data))
                : Results.Json(await MembershipNative.RunOnboardStep(onboardStep, req.InstanceId, req.Seq, req.Kind, data));
        });
        await govHost.StartAsync();

        // The portable flow's /step+/terminate callback host, on its OWN port so it coexists with /gov-step above.
        // Only renewal uses the portable flow here (onboarding is native), so it binds the Renew operation.
        WebApplication portableHost = RestateWorkflowHost.Build(portableStepUrl, renewStep, termination, token);
        await portableHost.StartAsync();

        // Wire the seam: native onboarding (start + raise on MembershipOnboard) and native offboarding (the
        // consumer-authored fan-out service); portable renewal via the generic MembershipPortable flow.
        system.Seam.Connect("onboard", new RestateWorkflowGateway(new Uri(ingress), "MembershipOnboard"),
            new WorkflowSealer(keys, system.Serializer, nameof(Native.IMembershipManager.Onboard)), system.Serializer);
        system.Seam.Connect("offboard", new RestateOffboardGateway(http, ingress),
            new WorkflowSealer(keys, system.Serializer, nameof(Native.IMembershipManager.Offboard)), system.Serializer);
        system.Seam.Connect("renew", new RestateWorkflowGateway(new Uri(ingress), "MembershipPortable"),
            new WorkflowSealer(keys, system.Serializer, nameof(Portable.IMembershipManager.Renew)), system.Serializer);

        WebApplicationBuilder webBuilder = MembershipWebHost.Create(port);
        var capabilities = new MembershipWebHost.Capabilities(
            Runtime: "Restate", Onboarding: true, Renewal: true, Offboarding: true, Restart: false,
            Dashboard: MembershipWebHost.DashboardFromEnv());
        WebApplication app = MembershipWebHost.Build(webBuilder, system, capabilities);

        app.Lifetime.ApplicationStopping.Register(() =>
        {
            try { govHost.StopAsync().GetAwaiter().GetResult(); } catch { /* shutting down */ }
            try { portableHost.StopAsync().GetAwaiter().GetResult(); } catch { /* shutting down */ }
            RestateSidecar.Kill(sidecar);
        });

        Console.WriteLine($"PiiMaker Restate control panel → http://localhost:{port}  (cross-language Restate sidecar)");
        await app.RunAsync();
    }
}
