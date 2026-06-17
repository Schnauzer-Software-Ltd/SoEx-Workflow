namespace SoEx.Workflow;

/// <summary>
/// An erasure request still in flight: the instances it left to complete naturally (trusting them to
/// self-erase before the statutory <see cref="Deadline"/>) that have not yet resolved. The deadline monitor
/// re-evaluates these as the deadline nears.
/// </summary>
public readonly record struct OpenErasureRequest(
    string RequestId, DateTimeOffset Deadline, IReadOnlyCollection<string> OpenInstanceIds, IReadOnlyList<string> Subjects);

/// <summary>
/// Records erasure requests that are not yet fully satisfied, so the maintenance backstop can re-evaluate
/// them against the advancing statutory clock — a request is otherwise fire-once, and a "complete naturally"
/// decision is never re-checked. A request is opened with its still-open instances, has instances resolved
/// off it as they self-erase, and is closed when none remain. In-process by default
/// (<c>InMemoryErasureRequestRegistry</c>); a durable, shared implementation lets a separately-hosted scheduler
/// monitor deadlines across the fleet and survive a restart.
/// </summary>
public interface IErasureRequestRegistry
{
    /// <summary>Opens (or replaces) a request with the instances still to resolve. Idempotent on the request id.</summary>
    void Open(OpenErasureRequest request);

    /// <summary>Marks one instance of a request resolved (self-erased / terminated); closes the request when none remain.</summary>
    void Resolve(string requestId, string instanceId);

    /// <summary>Closes a request outright.</summary>
    void Close(string requestId);

    /// <summary>A snapshot of every open request.</summary>
    IReadOnlyCollection<OpenErasureRequest> Open();
}
