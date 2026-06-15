using Autofac;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SoEx;   // Container.SetScopeAsyncLocal extension on ILifetimeScope

namespace PiiMaker.Hosting;

/// <summary>
/// Per-request middleware that establishes the SoEx ambient container scope so that
/// <c>Proxy.ForService&lt;I&gt;()</c> (and therefore the generated controllers) resolve the composed system.
/// In a SoEx ASP.NET method this is also where authenticated claims are mapped into an ambient
/// <c>AuthContext</c>; the examples run unauthenticated, so this is the minimal form — the load-bearing line
/// is <c>scope.SetScopeAsyncLocal()</c>, which points the async-local resolution scope at the SoEx host root
/// scope (registered as a singleton) where the entry proxy lives.
/// </summary>
public sealed class SoExContextMiddleware(RequestDelegate next, ILifetimeScope scope)
{
    public async Task InvokeAsync(HttpContext httpContext)
    {
        scope.SetScopeAsyncLocal();
        await next(httpContext);
    }
}

public static class SoExContextMiddlewareExtensions
{
    /// <summary>Sets the SoEx ambient scope for each request so generated controllers can dispatch.</summary>
    public static IApplicationBuilder UseSoContext(this IApplicationBuilder builder) =>
        builder.UseMiddleware<SoExContextMiddleware>();
}
