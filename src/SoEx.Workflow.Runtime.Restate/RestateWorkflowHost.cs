using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SoEx.Workflow;

namespace SoEx.Workflow.Runtime.Restate;

/// <summary>
/// Portable flow — the .NET half of the Restate generic sidecar. Restate ships no .NET SDK, so
/// the portable orchestration runs out-of-process (the Rust <c>OnboardWorkflow</c> in restate-sidecar-rs/)
/// and calls back to this host, which runs one governed step (or the termination lifecycle) per call:
///
///   POST /step       run one entrypoint step  -> flattened <see cref="WorkflowAction"/> as JSON
///   POST /terminate  run the termination erasure lifecycle (crypto-shred + index prune)
///
/// It is the HTTP analogue of the DurableTask <c>StepActivity</c>/<c>TerminateActivity</c>. Payloads stay
/// opaque base64; the sidecar never interprets them. JSON uses the ASP.NET web defaults (camelCase), matching
/// the sidecar's keys. Use this or the native flow (the Rust <c>NativeOnboardWorkflow</c> calling
/// a consumer-authored <c>/gov-step</c>+<c>/gov-terminate</c> host) — never both for one instance.
/// </summary>
public static class RestateWorkflowHost
{
    public sealed record StepRequest(byte[] Payload, string InstanceId, long Sequence);

    public sealed record TerminateRequest(string InstanceId, long Sequence);

    /// <summary>Flattened, language-agnostic view of a <see cref="WorkflowAction"/> for the sidecar.</summary>
    public sealed record ActionDto(string Kind, byte[] Payload, string EventName, long TimeoutTicks, byte[] OnTimeout, byte[] OnEvent);

    /// <summary>The largest step/terminate request body accepted (envelopes are small; this caps abuse).</summary>
    private const long MaxRequestBodyBytes = 4 * 1024 * 1024;

    /// <summary>
    /// The wire-contract version this host speaks. The Rust sidecar sends it on every call in the
    /// <see cref="WireVersionHeader"/> header and this host refuses a mismatch, so a stale sidecar binary cannot
    /// silently run an old contract. Bump it on any breaking change to the <c>/step</c>/<c>/terminate</c> shapes,
    /// and rebuild the sidecar in lock-step.
    /// </summary>
    public const string WireVersion = "1";

    /// <summary>The header the sidecar carries its <see cref="WireVersion"/> in.</summary>
    public const string WireVersionHeader = "x-soex-wire-version";

    /// <summary>
    /// Builds the Kestrel step host. <paramref name="stepUrl"/> is where the Restate sidecar reaches back
    /// (STEP_URL), e.g. <c>http://127.0.0.1:9090</c>. <paramref name="authToken"/> is the shared secret
    /// the sidecar must present as <c>Authorization: Bearer &lt;token&gt;</c>; the endpoints run governed
    /// steps and crypto-shred, so an unauthenticated caller must never reach them.
    /// <para>
    /// The bearer token (and request bodies, though those are sealed ciphertext) cross this hop in the clear
    /// over plain HTTP, so keep it on loopback, or secure it: pass an <c>https://</c> <paramref name="stepUrl"/>
    /// and either supply <paramref name="serverCertificate"/> here or configure the certificate through the
    /// standard ASP.NET Core Kestrel config (<c>Kestrel:Certificates:Default</c> /
    /// <c>ASPNETCORE_Kestrel__Certificates__Default__Path</c>); point the sidecar at that <c>https</c> URL
    /// (with <c>STEP_CA_CERT</c> for a private CA). This seam is internal to the runtime, not a public API.
    /// </para>
    /// </summary>
    public static WebApplication Build(string stepUrl, IGovernedStep step, GovernedTermination termination, string authToken,
        X509Certificate2? serverCertificate = null)
    {
        if (string.IsNullOrEmpty(authToken))
        {
            throw new ArgumentException("an auth token is required — the step host must not run unauthenticated", nameof(authToken));
        }

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(stepUrl);
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Limits.MaxRequestBodySize = MaxRequestBodyBytes;
            if (serverCertificate is not null)
            {
                // Explicit TLS: terminate HTTPS with the supplied certificate (an https stepUrl is required for
                // this to take effect). Without it, an https stepUrl falls back to Kestrel's default certificate
                // resolution (config/env), and an http stepUrl stays plaintext.
                o.ConfigureHttpsDefaults(h => h.ServerCertificate = serverCertificate);
            }
        });
        builder.Logging.ClearProviders();

        var app = builder.Build();

        byte[] expected = Encoding.UTF8.GetBytes(authToken);
        app.Use(async (context, next) =>
        {
            if (!IsAuthorized(context.Request, expected))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            // Wire-contract handshake: the sidecar sends its contract version on every call, and this host
            // refuses a mismatch (or a sidecar too old to send it). Without this a stale sidecar binary silently
            // runs the old contract against a new host — the exact failure the sidecar README warned about.
            string presentedVersion = context.Request.Headers[WireVersionHeader].ToString();
            if (presentedVersion != WireVersion)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync(
                    $"wire-contract version mismatch: this host speaks '{WireVersion}', the sidecar sent '{(presentedVersion.Length == 0 ? "(none)" : presentedVersion)}'. Rebuild the Restate sidecar against this host.");
                return;
            }

            await next();
        });

        app.MapPost("/step", async (StepRequest req) =>
        {
            // Compute the ambient and guard the in-clear instance id (journaled by Restate, which the framework
            // cannot intercept) INSIDE the try — so a throw here (a decrypt failure, or an id carrying the
            // subject) is scrubbed by the same catch instead of surfacing to the sidecar's durable state in clear.
            byte[]? ambient = null;
            WorkflowAction action;
            try
            {
                ambient = step.AmbientOf(req.InstanceId, req.Payload);
                step.GuardVisibleName(req.InstanceId, ambient);
                action = (await step.DispatchGovernedAsync(req.Payload, req.InstanceId, req.Sequence)) as WorkflowAction
                    ?? throw new InvalidOperationException($"the '{step.OperationName}' operation did not return a {nameof(WorkflowAction)}");
            }
            catch (Exception ex) when (!GovernedStepFailure.IsJournalSafe(step, req.InstanceId, ambient, ex))
            {
                // A thrown step surfaces to the Restate sidecar as the failure detail; scrub a message carrying a
                // subject id (never chained) so it cannot reach Restate's durable invocation state in clear.
                throw new InvalidOperationException(GovernedStepFailure.WithheldMessage);
            }
            return Results.Json(Flatten(step, req.InstanceId, action, ambient));
        });

        app.MapPost("/terminate", async (TerminateRequest req) =>
        {
            var key = new IdempotencyKey(req.InstanceId, "terminal", req.Sequence);
            await termination.TerminateAsync(req.InstanceId, key, TerminationTrigger.NaturalCompletion);
            return Results.Ok();
        });

        return app;
    }

    /// <summary>Constant-time check of the <c>Authorization: Bearer &lt;token&gt;</c> header against the shared secret.</summary>
    private static bool IsAuthorized(HttpRequest request, byte[] expected)
    {
        string header = request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.Ordinal))
        {
            return false;
        }

        byte[] presented = Encoding.UTF8.GetBytes(header[scheme.Length..]);
        return CryptographicOperations.FixedTimeEquals(presented, expected);
    }

    private static ActionDto Flatten(IGovernedStep step, string instanceId, WorkflowAction action, byte[]? ambient) => action switch
    {
        WorkflowAction.Complete c => new("complete", step.GuardResultPiiFree(step.Serializer.Serialize(c.Result), ambient), "", 0, [], []),
        WorkflowAction.RaiseIntoNext r => new("next", step.SealStep(instanceId, r.NextStep, ambient), "", 0, [], []),
        WorkflowAction.WaitForEvent w => new(
            "wait",
            [],
            step.GuardVisibleName(w.EventName, ambient),
            w.Timeout?.Ticks ?? -1,
            w.OnTimeout is { } ot ? step.SealStep(instanceId, ot, ambient) : [],
            w.OnEvent is { } oe ? step.SealStep(instanceId, oe, ambient) : []),
        WorkflowAction.Delay d => new("delay", [], "", d.Duration.Ticks, [], []),
        WorkflowAction.Loop l => new("loop", step.SealStep(instanceId, l.CarryState, ambient), "", 0, [], []),
        _ => throw new InvalidOperationException($"unhandled action {action.Kind()}"),
    };
}
