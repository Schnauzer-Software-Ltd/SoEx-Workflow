using Autofac;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SoEx.Abstractions;
using SoEx.Method.Workflow;

namespace PiiMaker.Hosting;

/// <summary>
/// The shared web control panel: stands up Kestrel, hosts the generator-emitted <c>IMembershipEntry</c>
/// controller (one POST per trigger), serves the static button UI, and exposes a few example-only endpoints
/// (host capabilities, scenario toggles, instance status, erasure). Every per-runtime host reuses this; the
/// only host-specific code is wiring the workflow seam to a runtime and any extra endpoints (e.g. Elsa's
/// restart-host button), supplied via <paramref name="hostEndpoints"/>.
/// </summary>
public static class MembershipWebHost
{
    /// <summary>What this host can drive — the UI hides cards a runtime/mode can't exercise. <see cref="Dashboard"/>,
    /// when present, points the UI at this runtime's backend dashboard (proxied alongside the panel).</summary>
    public sealed record Capabilities(string Runtime, bool Onboarding, bool Renewal, bool Offboarding, bool Restart,
        DashboardInfo? Dashboard = null);

    /// <summary>Where a runtime's backend dashboard is reachable, relative to wherever the panel was loaded from:
    /// the UI builds the link as <c>{scheme}://{page host}:{Port}</c>. <see cref="Https"/> is needed for
    /// dashboards that require a secure context (e.g. the DTS dashboard's WebCrypto/MSAL use).</summary>
    public sealed record DashboardInfo(int Port, bool Https);

    /// <summary>Reads the optional dashboard endpoint from the environment (PIIMAKER_DASHBOARD_PORT /
    /// PIIMAKER_DASHBOARD_SCHEME); returns null when unset, so a plain `dotnet run` shows no dashboard link.
    /// A deployment that fronts the dashboards (e.g. the Caddy setup in the README) sets these per host.</summary>
    public static DashboardInfo? DashboardFromEnv() =>
        int.TryParse(Environment.GetEnvironmentVariable("PIIMAKER_DASHBOARD_PORT"), out int port)
            ? new DashboardInfo(port, string.Equals(Environment.GetEnvironmentVariable("PIIMAKER_DASHBOARD_SCHEME"), "https", StringComparison.OrdinalIgnoreCase))
            : null;

    public static WebApplicationBuilder Create(int port)
    {
        // Content root = the output directory so wwwroot resolves next to the DLL regardless of the cwd
        // `dotnet run` happens to use. Each host links the shared wwwroot into its own output.
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.UseUrls($"http://localhost:{port}");
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o => o.SingleLine = true);

        builder.Services
            .AddControllers()
            // The generated controllers live in this (PiiMaker.Hosting) assembly, not the host exe.
            .AddApplicationPart(typeof(MembershipWebHost).Assembly)
            .AddJsonOptions(o => o.JsonSerializerOptions.TypeInfoResolver = new SoExTypeInfoResolver());

        // One served page drives any running host through a base-URL dropdown.
        builder.Services.AddCors(c => c.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
        return builder;
    }

    public static WebApplication Build(
        WebApplicationBuilder builder,
        MembershipSystem.Composition system,
        Capabilities capabilities,
        Action<WebApplication>? hostEndpoints = null)
    {
        // The SoEx host root scope (where the entry proxy is registered) that the middleware sets async-local.
        builder.Services.AddSingleton(system.Scope);

        WebApplication app = builder.Build();

        app.UseCors();
        app.UseSoContext();        // establish the ambient SoEx scope so Proxy.ForService<I>() resolves
        app.UseDefaultFiles();     // serve wwwroot/index.html at "/"
        app.UseStaticFiles();
        app.MapControllers();      // the generated POST /IMembershipEntry/{action} endpoints

        ExampleEndpoints.Map(app, system, capabilities);
        hostEndpoints?.Invoke(app);

        // The built-in erasure-maintenance runner (sweep abandoned + re-drive held + review deadlines), driven
        // on a timer (fire-and-forget for the demo), gated by PIIMAKER_MAINTENANCE. For production, leave it
        // OFF and host a dedicated scheduler separately that calls the utility's one-pass operations so exactly
        // one node drives each pass — this default does no leader election.
        _ = WorkflowMaintenance.RunAsync(system.Workflow, MaintenanceOptions(), app.Lifetime.ApplicationStopping);

        return app;
    }

    private static WorkflowMaintenanceOptions MaintenanceOptions()
    {
        bool enabled = (Environment.GetEnvironmentVariable("PIIMAKER_MAINTENANCE") ?? "on").Trim().ToLowerInvariant()
            is not ("off" or "false" or "0");

        // Demo cadences: each pass also runs once immediately at startup. olderThan is set well above any demo
        // flow's lifetime so a live in-flight instance is never swept.
        return new WorkflowMaintenanceOptions
        {
            Enabled = enabled,
            SweepInterval = TimeSpan.FromMinutes(15),
            SweepOlderThan = TimeSpan.FromDays(1),
            HeldReDriveInterval = TimeSpan.FromMinutes(1),
            DeadlineReviewInterval = TimeSpan.FromMinutes(1),
            EscalateWithin = TimeSpan.FromDays(1),
        };
    }
}
