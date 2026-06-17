namespace SoEx.Workflow;

/// <summary>
/// The at-least-once idempotency key for a step: an effect keyed on this triple
/// is applied once however many times the step is delivered.
/// </summary>
public readonly record struct IdempotencyKey(string InstanceId, string DtoType, long Sequence)
{
    public override string ToString() => $"{InstanceId}/{DtoType}/{Sequence}";
}
