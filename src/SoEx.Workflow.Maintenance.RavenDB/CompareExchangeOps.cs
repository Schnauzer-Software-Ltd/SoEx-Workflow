using Raven.Client.Documents;
using Raven.Client.Documents.Operations.CompareExchange;

namespace SoEx.Workflow.Maintenance.RavenDB;

/// <summary>Small synchronous compare-exchange helpers shared by the two maintenance logs (upsert/delete/list).</summary>
internal static class CompareExchangeOps
{
    public static void Upsert<T>(IDocumentStore store, string key, T value)
    {
        for (int attempt = 0; ; attempt++)
        {
            CompareExchangeValue<T>? current = store.Operations.Send(new GetCompareExchangeValueOperation<T>(key));
            CompareExchangeResult<T> put = store.Operations.Send(
                new PutCompareExchangeValueOperation<T>(key, value, current?.Index ?? 0));
            if (put.Successful)
            {
                return; // wrote at the expected index.
            }
            // A concurrent writer moved the index — back off (jittered) so retriers on a hot key disperse
            // instead of re-colliding in lockstep, then re-read and retry against the new index.
            Backoff(attempt);
        }
    }

    public static T? Read<T>(IDocumentStore store, string key) where T : class =>
        store.Operations.Send(new GetCompareExchangeValueOperation<T>(key))?.Value;

    /// <summary>
    /// Atomic read-modify-write: re-reads the value and re-applies <paramref name="transform"/> against the
    /// CURRENT value until the compare-exchange lands, so concurrent mutations never lose an update. The
    /// transform returns the next value to store, or <c>null</c> to delete the entry. A no-op if the key is
    /// absent. (A plain read-then-<see cref="Upsert"/> would re-write a stale value and silently drop a
    /// concurrent change.)
    /// </summary>
    public static void Mutate<T>(IDocumentStore store, string key, Func<T, T?> transform) where T : class
    {
        for (int attempt = 0; ; attempt++)
        {
            CompareExchangeValue<T>? current = store.Operations.Send(new GetCompareExchangeValueOperation<T>(key));
            if (current?.Value is not { } value)
            {
                return; // absent — nothing to mutate.
            }

            T? next = transform(value);
            bool landed = next is null
                ? store.Operations.Send(new DeleteCompareExchangeValueOperation<T>(key, current.Index)).Successful
                : store.Operations.Send(new PutCompareExchangeValueOperation<T>(key, next, current.Index)).Successful;
            if (landed)
            {
                return;
            }
            // A concurrent writer moved the index — back off (jittered), then re-read and re-apply.
            Backoff(attempt);
        }
    }

    public static void Delete<T>(IDocumentStore store, string key)
    {
        for (int attempt = 0; ; attempt++)
        {
            CompareExchangeValue<T>? current = store.Operations.Send(new GetCompareExchangeValueOperation<T>(key));
            if (current is null)
            {
                return; // already gone.
            }

            if (store.Operations.Send(new DeleteCompareExchangeValueOperation<T>(key, current.Index)).Successful)
            {
                return;
            }
            // Lost the index race to a concurrent writer — back off (jittered), then re-read and retry.
            Backoff(attempt);
        }
    }

    // Exponential backoff capped at ~25ms, fully jittered, so concurrent retriers on a hot compare-exchange key
    // spread out instead of re-colliding in lockstep (a tight immediate-retry spin thunders under contention).
    private static void Backoff(int attempt) =>
        Thread.Sleep(Random.Shared.Next(1, Math.Min(25, 1 << Math.Min(attempt, 4)) + 1));

    public static IEnumerable<T> List<T>(IDocumentStore store, string startsWith) where T : class
    {
        const int page = 256;
        int start = 0;
        while (true)
        {
            Dictionary<string, CompareExchangeValue<T>> values = store.Operations.Send(
                new GetCompareExchangeValuesOperation<T>(startsWith, start, page));
            foreach (CompareExchangeValue<T> v in values.Values)
            {
                if (v.Value is { } value)
                {
                    yield return value;
                }
            }

            if (values.Count < page)
            {
                yield break;
            }

            start += page;
        }
    }
}
