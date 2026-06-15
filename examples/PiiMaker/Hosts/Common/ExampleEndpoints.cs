using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SoEx.Method.Workflow.External;

namespace PiiMaker.Hosting;

/// <summary>
/// Example-only HTTP endpoints that sit alongside the generated trigger controller. They are NOT part of the
/// lifted service contract — they exist so the static UI can show what the hardcoded console drivers used to
/// do internally: toggle a billing decline (to drive the dunning branch), read instance status (PII-free, by
/// the instance id a Start call returned), and run a right-to-erasure sweep. Hand-written minimal-API on
/// purpose: observability and test affordances should not leak into the service interface the generator lifts.
/// </summary>
public static class ExampleEndpoints
{
    public static void Map(
        WebApplication app,
        MembershipSystem.Composition system,
        MembershipWebHost.Capabilities capabilities)
    {
        // What this host can drive — the UI capability-gates its cards from this.
        app.MapGet("/example/host", () => Results.Json(capabilities));

        // Scenario: mark a (subscriber, period) charge to decline so the renewal dunning path is demonstrable
        // (what RunSubscription set as `system.Billing.Declines[(subject, 1L)] = 0` before StartRenewal).
        app.MapPost("/example/billing/decline", (DeclineRequest r) =>
        {
            system.Billing.Declines[(r.Subject, r.Period)] = 0;
            return Results.Ok(new { declined = true, r.Subject, r.Period });
        });
        app.MapPost("/example/billing/clear", (DeclineRequest r) =>
        {
            system.Billing.Declines.TryRemove((r.Subject, r.Period), out _);
            return Results.Ok(new { cleared = true, r.Subject, r.Period });
        });
        app.MapGet("/example/billing/attempts", (string subject, long period) =>
            Results.Json(new { subject, period, attempts = system.Billing.Attempts.TryGetValue((subject, period), out int n) ? n : 0 }));

        // PII-free status: the client passes back the instance id a Start* call returned. A live per-instance
        // key means the flow is in flight; once it completes (or is erased) the key is crypto-shredded, so the
        // key flipping from live → gone is the public completion signal. No subject lookup is exposed here.
        app.MapGet("/example/status/{instanceId}", (string instanceId) =>
            Results.Json(new { instanceId, keyLive = system.Keys.Has(instanceId) }));

        // Flow D: right-to-erasure for a subject. Erasure is admit-and-drain: the host admits the request to the
        // utility's External face (returning a request id at once, nothing shredded yet), then a drain pass does
        // the per-manager crypto-shred. A production host schedules the drain (the built-in maintenance runner
        // does it by default); the panel drains inline so the button shows the shred immediately.
        app.MapPost("/example/erase", async (EraseRequest r) =>
        {
            string requestId = await system.Workflow.RequestEraseAsync(r.Subject);
            int drained = await system.Workflow.DrainEraseRequestsAsync();
            return Results.Json(new { subject = r.Subject, requestId, drained });
        });
    }

    public sealed record DeclineRequest(string Subject, long Period);
    public sealed record EraseRequest(string Subject);
}
