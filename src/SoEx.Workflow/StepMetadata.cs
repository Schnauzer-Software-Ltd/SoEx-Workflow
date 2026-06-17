namespace SoEx.Workflow;

/// <summary>
/// Framework-understood facts surfaced from a step invocation for framework
/// operations (subject indexing, erasure routing, idempotency). Runtime adapters
/// use this and never interpret the opaque payload.
/// </summary>
public sealed record StepMetadata(
    string InstanceId,
    long Sequence,
    string DtoType,
    IReadOnlyList<string> SubjectIds,
    bool WorkflowManaged)
{
    public IdempotencyKey IdempotencyKey => new(InstanceId, DtoType, Sequence);
}
