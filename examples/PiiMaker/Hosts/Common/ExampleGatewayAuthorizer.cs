using SoEx.Workflow;

namespace PiiMaker.Hosting;

/// <summary>
/// A worked <see cref="IGatewayAuthorizer"/>: the chokepoint every gateway consults before it starts a
/// flow or raises an event. This one checks an ambient bearer token against an allow-predicate; a production host
/// would resolve the caller's identity from the request and apply its own policy. Throwing rejects the
/// operation before the runtime is touched; returning allows it.
/// <para>
/// The example panels construct it in <c>AllowAll</c> mode so the interactive buttons keep working without a
/// token; pass a production <paramref name="isAuthorized"/> to enforce.
/// </para>
/// </summary>
public sealed class ExampleGatewayAuthorizer(Func<string?> currentToken, Func<string?, bool> isAuthorized)
    : IGatewayAuthorizer
{
    /// <summary>An authorizer that permits every operation — the default for the interactive examples.</summary>
    public static ExampleGatewayAuthorizer AllowAll { get; } = new(() => null, _ => true);

    public Task AuthorizeStartAsync(string instanceId) => Authorize($"start {instanceId}");

    public Task AuthorizeRaiseEventAsync(string instanceId, string eventName) => Authorize($"raise {eventName} @ {instanceId}");

    private Task Authorize(string operation) =>
        isAuthorized(currentToken())
            ? Task.CompletedTask
            : throw new UnauthorizedAccessException($"gateway operation not authorized: {operation}");
}
