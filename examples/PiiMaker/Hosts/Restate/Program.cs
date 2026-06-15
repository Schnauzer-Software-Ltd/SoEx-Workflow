using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using PiiMaker.Hosting;
using PiiMaker.Manager.Membership.Interface;
using PiiMaker.Manager.Membership.Service;
using SoEx.Workflow;
using SoEx.Workflow.InMemory;
using SoEx.Workflow.Restate;

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
// Requires restate-server (ingress :8088, admin :9070) running (Docker). The Restate sidecar is built (cargo) and
// spawned by this host, then killed on exit. Only start this host when restate-server is up.
// Run: dotnet run --project examples/PiiMaker/Hosts/Restate -- [port]  (default 5004), then open http://localhost:5004
// =============================================================================================

const string ingress = "http://localhost:8088", admin = "http://localhost:9070";
const string sidecarUri = "http://127.0.0.1:9081";   // example sidecar endpoint (the test sidecar uses :9080)
const string stepUrl = "http://127.0.0.1:9091";     // native /gov-step callback host (test uses :9090)
const string portableStepUrl = "http://127.0.0.1:9092";   // portable /step+/terminate callback host (renewal)
const int sidecarPort = 9081;
const string token = "pii-example-token";           // shared secret: Restate sidecar <-> .NET callback host
const string subSystem = "membership";
int port = args is [var p, ..] && int.TryParse(p, out int n) ? n : 5004;

var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

if (!Reachable("localhost", 8088) || !Reachable("localhost", 9070))
{
    Console.WriteLine("✗ restate-server not reachable on :8088/:9070; start it (Docker) and retry.");
    return;
}
if (Reachable("127.0.0.1", sidecarPort))
{
    Console.WriteLine($"✗ something is already listening on :{sidecarPort}; stop the other sidecar and retry.");
    return;
}

// Build (if needed) + spawn the example's own Restate sidecar, then point restate at it. It stays up the whole run.
string sidecarDir = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
    .First(a => a.Key == "RestateSidecarRsDir").Value!;
string sidecarBin = Path.Combine(sidecarDir, "target", "release", "piimaker-sidecar");
if (!File.Exists(sidecarBin)) TryBuildSidecar(sidecarDir);
if (!File.Exists(sidecarBin))
{
    Console.WriteLine("✗ Restate sidecar binary missing and `cargo build --release` unavailable/failed; install cargo and retry.");
    return;
}

Process sidecar = StartSidecar(sidecarBin, sidecarDir);
if (!WaitForPort(sidecarPort, TimeSpan.FromSeconds(20)))
{
    Console.WriteLine($"✗ the Restate sidecar did not start listening on :{sidecarPort}.");
    KillSidecar(sidecar);
    return;
}
HttpResponseMessage reg = await http.PostAsync($"{admin}/deployments", JsonBody(new { uri = sidecarUri, force = true }));
if (!reg.IsSuccessStatusCode)
{
    Console.WriteLine($"✗ restate could not discover the Restate sidecar ({(int)reg.StatusCode}).");
    KillSidecar(sidecar);
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
GovernedStep<IMembershipManager> StepFor(string op) => new(system.Endpoint, system.Serializer, idempotency, keys, index, op);

var onboardStep = StepFor(nameof(IMembershipManager.OnboardStep));
var offboardStep = StepFor(nameof(IMembershipManager.OffboardStep));
var renewStep = StepFor(nameof(IMembershipManager.Renew));

// One long-lived governed-step callback host: both native Rust flows call back here. The onboarding and
// offboarding flows are told apart by the instance-id prefix the entry seam derived (onboard- / offboard-),
// dispatching to the matching governed operation. Stays up for the whole process.
WebApplication govHost = BuildGovStepHost(async req =>
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
    new WorkflowSealer(keys, system.Serializer, nameof(IMembershipManager.OnboardStep)), system.Serializer);
system.Seam.Connect("offboard", new RestateOffboardGateway(http, ingress),
    new WorkflowSealer(keys, system.Serializer, nameof(IMembershipManager.OffboardStep)), system.Serializer);
system.Seam.Connect("renew", new RestateWorkflowGateway(new Uri(ingress), "MembershipPortable"),
    new WorkflowSealer(keys, system.Serializer, nameof(IMembershipManager.Renew)), system.Serializer);

WebApplicationBuilder webBuilder = MembershipWebHost.Create(port);
var capabilities = new MembershipWebHost.Capabilities(
    Runtime: "Restate", Onboarding: true, Renewal: true, Offboarding: true, Restart: false,
    Dashboard: MembershipWebHost.DashboardFromEnv());
WebApplication app = MembershipWebHost.Build(webBuilder, system, capabilities);

app.Lifetime.ApplicationStopping.Register(() =>
{
    try { govHost.StopAsync().GetAwaiter().GetResult(); } catch { /* shutting down */ }
    try { portableHost.StopAsync().GetAwaiter().GetResult(); } catch { /* shutting down */ }
    KillSidecar(sidecar);
});

Console.WriteLine($"PiiMaker Restate control panel → http://localhost:{port}  (cross-language Restate sidecar)");
await app.RunAsync();

// ---- a consumer-authored native governed-step callback host (/gov-step + /gov-terminate) -------
WebApplication BuildGovStepHost(Func<GovStepRequest, Task<IResult>> govStep)
{
    var b = WebApplication.CreateBuilder();
    b.WebHost.UseUrls(stepUrl);
    b.Logging.ClearProviders();
    WebApplication govApp = b.Build();

    byte[] expected = Encoding.UTF8.GetBytes(token);
    govApp.Use(async (ctx, next) =>
    {
        string h = ctx.Request.Headers.Authorization.ToString();
        if (!h.StartsWith("Bearer ", StringComparison.Ordinal) ||
            !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(h["Bearer ".Length..]), expected))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        await next();
    });

    govApp.MapPost("/gov-step", async (GovStepRequest req) => await govStep(req));
    govApp.MapPost("/gov-terminate", async (GovTerminateRequest req) =>
    {
        await termination.TerminateAsync(req.InstanceId, new IdempotencyKey(req.InstanceId, "terminal", 0), TerminationTrigger.NaturalCompletion);
        return Results.Ok();
    });
    return govApp;
}

static StringContent JsonBody(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

static bool Reachable(string host, int port)
{
    try { using var t = new TcpClient(); return t.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(2)) && t.Connected; }
    catch { return false; }   // refused/timeout (Wait wraps SocketException in AggregateException) → not reachable
}

static bool WaitForPort(int port, TimeSpan timeout)
{
    DateTime deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        if (Reachable("127.0.0.1", port)) return true;
        Thread.Sleep(250);
    }
    return false;
}

static void TryBuildSidecar(string dir)
{
    string cargo = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cargo", "bin", "cargo");
    if (!File.Exists(cargo)) cargo = "cargo";
    try
    {
        using Process? build = Process.Start(new ProcessStartInfo(cargo, "build --release")
        { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true });
        build?.WaitForExit((int)TimeSpan.FromMinutes(8).TotalMilliseconds);
    }
    catch { /* cargo unavailable — caller reports when the binary is still absent */ }
}

static Process StartSidecar(string bin, string dir)
{
    var psi = new ProcessStartInfo(bin) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
    psi.Environment["STEP_URL"] = "http://127.0.0.1:9091";
    psi.Environment["PORTABLE_STEP_URL"] = "http://127.0.0.1:9092";   // MembershipPortable -> the renewal /step host
    psi.Environment["STEP_TOKEN"] = "pii-example-token";
    psi.Environment["BIND"] = "127.0.0.1:9081";
    Process proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start the Restate sidecar");
    proc.OutputDataReceived += (_, _) => { };
    proc.ErrorDataReceived += (_, _) => { };
    proc.BeginOutputReadLine();
    proc.BeginErrorReadLine();
    return proc;
}

static void KillSidecar(Process sidecar)
{
    try { if (!sidecar.HasExited) { sidecar.Kill(entireProcessTree: true); sidecar.WaitForExit(3000); } } catch { /* best-effort */ }
    sidecar.Dispose();
}

// The gateway that submits the consumer-authored native offboarding fan-out (a Restate service call,
// fire-and-forget via the ingress /send suffix). The fan-out completes on its own and shreds at the termination,
// so there are no continuation events.
sealed class RestateOffboardGateway(HttpClient http, string ingress) : IWorkflowGateway
{
    public async Task StartAsync(string instanceId, byte[] sealedSeed)
    {
        var body = new StringContent(JsonSerializer.Serialize(Convert.ToBase64String(sealedSeed)), Encoding.UTF8, "application/json");
        (await http.PostAsync($"{ingress}/MembershipOffboard/{instanceId}/run/send", body)).EnsureSuccessStatusCode();
    }

    public Task RaiseEventAsync(string instanceId, string eventName, byte[]? sealedPayload = null, string? raiseId = null) =>
        throw new NotSupportedException("the offboarding fan-out completes on its own; it has no continuation events");
}

// The native sidecar's callback payloads (camelCase on the wire, bound by the ASP.NET minimal-API binder).
public sealed record GovStepRequest(string InstanceId, long Seq, string Kind, string Data);
public sealed record GovTerminateRequest(string InstanceId);
