namespace PiiMaker.Access.Retention.Interface;

/// <summary>
/// The consumer's own governed store for must-retain carve-outs (the executed contract, the invoice tail,
/// the employment record). A SoEx component contract; Task-returning. <c>OnRetaining</c> writes here —
/// <b>outward</b> of workflow state, while the per-instance key is still live — so the record survives the
/// termination crypto-shred. Writes are idempotent on the idempotency key.
/// </summary>
public interface IRetainedRecordAccess
{
    /// <summary>Persists a must-retain record for an instance (idempotent on <paramref name="idempotencyKey"/>).</summary>
    Task RetainAsync(string idempotencyKey, string record);
}
