using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SoEx.Workflow;

namespace PiiMaker.Host.Restate;

/// <summary>
/// A consumer-authored native governed-step callback host (<c>/gov-step</c> + <c>/gov-terminate</c>) that both
/// Rust flows call back into. Bearer-token gated; <c>/gov-step</c> runs one governed step (the caller routes by
/// instance-id prefix), <c>/gov-terminate</c> runs the crypto-shred termination.
/// </summary>
internal static class GovStepHost
{
    public static WebApplication Build(
        string url, string token, GovernedTermination termination, Func<GovStepRequest, Task<IResult>> govStep)
    {
        var b = WebApplication.CreateBuilder();
        b.WebHost.UseUrls(url);
        b.Logging.ClearProviders();
        WebApplication app = b.Build();

        byte[] expected = Encoding.UTF8.GetBytes(token);
        app.Use(async (ctx, next) =>
        {
            string h = ctx.Request.Headers.Authorization.ToString();
            if (!h.StartsWith("Bearer ", StringComparison.Ordinal) ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(h["Bearer ".Length..]), expected))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next();
        });

        app.MapPost("/gov-step", async (GovStepRequest req) => await govStep(req));
        app.MapPost("/gov-terminate", async (GovTerminateRequest req) =>
        {
            await termination.TerminateAsync(
                req.InstanceId, new IdempotencyKey(req.InstanceId, "terminal", 0), TerminationTrigger.NaturalCompletion);
            return Results.Ok();
        });
        return app;
    }
}
