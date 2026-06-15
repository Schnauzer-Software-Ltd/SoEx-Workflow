namespace SoEx.Workflow;

/// <summary>
/// The at-least-once idempotency key for a step: an effect keyed on this triple
/// is applied once however many times the step is delivered.
/// </summary>
public readonly record struct IdempotencyKey(string InstanceId, string DtoType, long Sequence)
{
    public override string ToString() => $"{InstanceId}/{DtoType}/{Sequence}";
}

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
