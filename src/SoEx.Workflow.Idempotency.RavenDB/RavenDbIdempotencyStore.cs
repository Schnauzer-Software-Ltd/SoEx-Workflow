using Raven.Client.Documents;
using Raven.Client.Documents.Operations.CompareExchange;

namespace SoEx.Workflow.Idempotency.RavenDB;

/// <summary>
/// A durable, shared <see cref="IIdempotencyStore"/> over RavenDB compare-exchange — the durable analogue of
/// <c>InMemoryIdempotencyStore</c>. An effect keyed on an <see cref="IdempotencyKey"/> runs <b>exactly once</b>
/// across processes, and a redelivery returns the recorded result without re-running it; a failed effect leaves
/// no record (a retry re-runs). RavenDB (the clustered component) is the single source of truth, so the
/// once-guarantee holds cross-process and across a restart.
/// <para>
/// Protocol: a <em>claim-first</em> compare-exchange. A worker create-only-claims a <c>pending</c> entry before
/// running the effect; the winner runs and writes <c>done(result)</c> at the claim's index; concurrent callers
/// of an in-flight key briefly poll for <c>done</c>. A claim older than <see cref="StealAfter"/> (a crashed
/// owner) is stolen and re-run — set it comfortably above the longest expected effect so a slow-but-live owner
/// is not falsely stolen. Uses the synchronous RavenDB <c>Operations.Send</c>; no key material is held.
/// </para>
/// </summary>
public sealed class RavenDbIdempotencyStore : IIdempotencyStore
{
    private const string Pending = "pending";
    private const string Done = "done";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly IDocumentStore _store;
    private readonly string _keyPrefix;
    private readonly TimeProvider _time;

    /// <param name="store">A connected RavenDB document store (the shared, durable backing store).</param>
    /// <param name="keyPrefix">Compare-exchange key prefix isolating this store's keys.</param>
    /// <param name="time">Clock for claim timestamps; defaults to <see cref="TimeProvider.System"/>.</param>
    public RavenDbIdempotencyStore(IDocumentStore store, string keyPrefix = "idem/", TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _keyPrefix = keyPrefix;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>How long a <c>pending</c> claim is honoured before a crashed owner is assumed and it is stolen.</summary>
    public TimeSpan StealAfter { get; init; } = TimeSpan.FromMinutes(2);

    public async Task<byte[]> ApplyOnceAsync(IdempotencyKey key, Func<Task<byte[]>> effect)
    {
        string cxKey = _keyPrefix + key;

        while (true)
        {
            CompareExchangeValue<Entry>? current =
                _store.Operations.Send(new GetCompareExchangeValueOperation<Entry>(cxKey));

            if (current?.Value is { } entry)
            {
                if (entry.State == Done)
                {
                    return entry.Result ?? []; // already applied — return the recorded result, do not re-run.
                }

                // pending: another worker is running. Wait it out unless the claim is stale (crashed owner).
                if (_time.GetUtcNow() - entry.At < StealAfter)
                {
                    await Task.Delay(PollInterval);
                    continue;
                }

                CompareExchangeResult<Entry> steal = _store.Operations.Send(
                    new PutCompareExchangeValueOperation<Entry>(cxKey, NewPending(), current.Index));
                if (!steal.Successful)
                {
                    continue; // lost the steal race — re-read and reassess.
                }

                return await RunAndCompleteAsync(cxKey, steal.Index, effect);
            }

            // absent: claim create-only (index 0).
            CompareExchangeResult<Entry> claim = _store.Operations.Send(
                new PutCompareExchangeValueOperation<Entry>(cxKey, NewPending(), 0));
            if (!claim.Successful)
            {
                continue; // a concurrent caller claimed first — re-read (will see pending/done).
            }

            return await RunAndCompleteAsync(cxKey, claim.Index, effect);
        }
    }

    private async Task<byte[]> RunAndCompleteAsync(string cxKey, long claimIndex, Func<Task<byte[]>> effect)
    {
        byte[] result;
        try
        {
            result = await effect();
        }
        catch
        {
            // Release the claim so a redelivery re-runs (the effect did not complete). If the delete is not
            // Successful our claim was already stolen (we exceeded StealAfter) — the stealer owns a fresh claim
            // and will re-run, so a lost release is the correct no-op here; nothing to recover.
            _store.Operations.Send(new DeleteCompareExchangeValueOperation<Entry>(cxKey, claimIndex));
            throw;
        }

        // Record done(result) at the claim's index. This is Successful unless our claim was stolen mid-effect
        // (another worker assumed us crashed after StealAfter and re-claimed at a new index). A stolen
        // done-write is not a silent loss: the stealer re-runs and records the canonical result, so on failure
        // re-read and return THAT, so every caller converges on the one recorded result rather than an orphaned
        // local copy that a later redelivery would not match.
        CompareExchangeResult<Entry> done = _store.Operations.Send(new PutCompareExchangeValueOperation<Entry>(
            cxKey, new Entry { State = Done, Result = result, At = _time.GetUtcNow() }, claimIndex));
        if (done.Successful)
        {
            return result;
        }

        CompareExchangeValue<Entry>? canonical =
            _store.Operations.Send(new GetCompareExchangeValueOperation<Entry>(cxKey));
        return canonical?.Value is { State: Done, Result: { } recorded } ? recorded : result;
    }

    private Entry NewPending() => new() { State = Pending, Result = null, At = _time.GetUtcNow() };

    /// <summary>The compare-exchange value: a claim that is either in flight (<c>pending</c>) or applied (<c>done</c>).</summary>
    internal sealed class Entry
    {
        public string State { get; set; } = Pending;
        public byte[]? Result { get; set; }
        public DateTimeOffset At { get; set; }
    }
}
