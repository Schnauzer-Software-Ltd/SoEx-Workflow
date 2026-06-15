using Elsa.Extensions;
using Elsa.Persistence.EFCore.Extensions;
using Elsa.Persistence.EFCore.Modules.Management;
using Elsa.Persistence.EFCore.Modules.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PiiMaker.Host.Elsa;
using PiiMaker.Hosting;
using PiiMaker.Manager.Membership.Interface;
using SoEx.Workflow;
using SoEx.Workflow.Elsa;
using SoEx.Workflow.InMemory;

// =============================================================================================
// Example host: ONBOARDING (flow A) on Elsa, NATIVE flow, DURABLE (permanent) persistence — as a web
// CONTROL PANEL. Elsa is an in-process engine, not a server; "permanent" means a persistence provider, here
// a SQLite store (EF Core). Durability is demonstrated interactively: start onboarding (it parks on the
// invite-accepted bookmark, persisted to SQLite), press "Restart host" to dispose the provider and build a
// FRESH one over the SAME database, then deliver invite-accepted — the saga resumes on the new process and
// completes. The in-memory governance (keys/index) is the shared durable vault across the restart.
// Run: dotnet run --project examples/PiiMaker/Hosts/Elsa -- [port]  (default 5005), then open http://localhost:5005
// =============================================================================================

int port = args is [var p, ..] && int.TryParse(p, out int n) ? n : 5005;

string db = $"/tmp/piimaker-elsa-{Guid.NewGuid():N}.db";
string conn = $"Data Source={db};Cache=Shared";

// Workflow governance + the membership system. The governed step is bound to the NATIVE OnboardStep
// operation; it and the termination are registered into each Elsa provider so rehydrated activities resolve them.
// The Workflow utility owns the durable governance stores; read them back to wire the governed step + termination.
MembershipSystem.Composition system = MembershipSystem.Compose("membership", MembershipPolicy.Default);
IInstanceKeyStore keys = system.Keys;
ISubjectIndex index = system.Index;
IIdempotencyStore idempotency = system.Idempotency;
var step = new GovernedStep<IMembershipManager>(
    system.Endpoint, system.Serializer, idempotency, keys, index, operationName: nameof(IMembershipManager.OnboardStep));
var termination = new GovernedTermination(system.Erasure, keys, index, system.HeldLog);

// The current Elsa provider (swapped out by the restart-host button). Onboarding flows through whichever
// provider is live; the SQLite store and the in-memory governance survive the swap.
ServiceProvider elsa = BuildHost(conn, step, termination);
await StartHostedAsync(elsa);

void Connect(ServiceProvider sp) => system.Seam.Connect("onboard",
    new ElsaWorkflowGateway(sp, nameof(MembershipOnboardWorkflow), idempotency: system.Idempotency),
    new WorkflowSealer(keys, system.Serializer, nameof(IMembershipManager.OnboardStep)), system.Serializer);
Connect(elsa);

WebApplicationBuilder webBuilder = MembershipWebHost.Create(port);
var capabilities = new MembershipWebHost.Capabilities(
    Runtime: "Elsa", Onboarding: true, Renewal: false, Offboarding: false, Restart: true);

WebApplication app = MembershipWebHost.Build(webBuilder, system, capabilities, host =>
{
    // Restart the host mid-flow: dispose the current provider and build a fresh one over the SAME SQLite DB,
    // then re-wire the seam. A parked onboarding saga resumes on the new provider when its event arrives.
    host.MapPost("/example/restart-host", async () =>
    {
        ServiceProvider old = elsa;
        elsa = BuildHost(conn, step, termination);
        await StartHostedAsync(elsa);
        Connect(elsa);
        await old.DisposeAsync();
        return Results.Ok(new { restarted = true });
    });
});

app.Lifetime.ApplicationStopping.Register(() =>
{
    try { elsa.Dispose(); } catch { /* shutting down */ }
    foreach (string f in Directory.GetFiles("/tmp", Path.GetFileName(db) + "*"))
    {
        try { File.Delete(f); } catch { /* best-effort cleanup */ }
    }
});

Console.WriteLine($"PiiMaker Elsa control panel → http://localhost:{port}  (durable SQLite: {db})");
await app.RunAsync();

// ---------------------------------------------------------------------------------------------
// A durable Elsa host: EF Core SQLite persistence + the registered workflow + the governed step/termination
// the rehydrated activities resolve from DI.
// ---------------------------------------------------------------------------------------------
static ServiceProvider BuildHost(string conn, GovernedStep<IMembershipManager> step, GovernedTermination termination) =>
    ElsaWorkflowHost.BuildDurable(step, termination, elsa =>
    {
        elsa.AddWorkflow<MembershipOnboardWorkflow>();
        elsa.UseWorkflowManagement(m => m.UseEntityFrameworkCore(ef => ef.UseSqlite(conn)));
        elsa.UseWorkflowRuntime(r => r.UseEntityFrameworkCore(ef => ef.UseSqlite(conn)));
    });

static async Task StartHostedAsync(IServiceProvider sp)
{
    foreach (IHostedService h in sp.GetServices<IHostedService>())
    {
        await h.StartAsync(default);
    }
}
