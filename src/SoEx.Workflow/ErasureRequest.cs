namespace SoEx.Workflow;

/// <summary>
/// An erasure ("forget subject S") request as it enters the framework. Its
/// <see cref="ReceivedAt"/> is the statutory clock anchor — distinct from any
/// instance's start time and from a first-failure time — and is propagated to every
/// instance the request fans out to. <see cref="Subjects"/> is the requested subject
/// set; empty means no subject context was supplied (reporting then degrades to the
/// instance-level floor).
/// </summary>
public sealed record ErasureRequest(string RequestId, DateTimeOffset ReceivedAt, IReadOnlyList<string> Subjects)
{
    public static ErasureRequest For(string requestId, DateTimeOffset receivedAt, params string[] subjects) =>
        new(requestId, receivedAt, subjects);
}
