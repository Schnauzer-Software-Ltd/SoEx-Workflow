namespace SoEx.Workflow;

/// <summary>
/// Absorbs at-least-once redelivery: an effect keyed on an <see cref="IdempotencyKey"/>
/// is applied once, and a redelivery returns the recorded result without re-running it.
/// </summary>
public interface IIdempotencyStore
{
    Task<byte[]> ApplyOnceAsync(IdempotencyKey key, Func<Task<byte[]>> effect);
}
