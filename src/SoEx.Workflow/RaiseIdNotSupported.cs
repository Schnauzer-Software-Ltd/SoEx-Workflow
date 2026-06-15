namespace SoEx.Workflow;

/// <summary>
/// Guards an adapter that does not implement idempotent raises (<c>raiseId</c> dedup). Rejecting a
/// non-null id is deliberately louder than silently ignoring it: a caller relying on dedup must not be
/// told it happened when it did not. Adapters that <i>do</i> dedup never reach this: InProc, the portable
/// Temporal and Durable Task drivers (per-instance handled-id set), Zeebe (broker message id), and Restate
/// (write-once durable promise per event name — dedup by construction). The Elsa gateway dedupes when an
/// <see cref="IIdempotencyStore"/> is wired into it (routing the resume through <c>ApplyOnceAsync</c>); this
/// is its <i>fallback</i> when no such store is wired — the gateway drives consumer-authored definitions, so
/// without a store there is no place to record handled ids.
/// </summary>
public static class RaiseIdNotSupported
{
    /// <summary>Throws <see cref="NotSupportedException"/> when <paramref name="raiseId"/> is set.</summary>
    public static void ThrowIfSet(string? raiseId, string adapter)
    {
        if (raiseId is not null)
        {
            throw new NotSupportedException(
                $"the {adapter} gateway does not implement idempotent raises yet; pass a null raiseId, " +
                "or make the OnEvent continuation idempotent");
        }
    }
}
