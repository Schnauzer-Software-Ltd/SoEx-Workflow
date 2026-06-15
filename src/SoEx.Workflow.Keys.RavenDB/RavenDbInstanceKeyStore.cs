using System.Security.Cryptography;
using System.Text;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.CompareExchange;

namespace SoEx.Workflow.Keys.RavenDB;

/// <summary>
/// A durable, shared <see cref="IInstanceKeyStore"/> over RavenDB compare-exchange. Per instance it stores a
/// random AES-256 data key (DEK) <em>wrapped</em> under a master key (KEK) the consumer supplies; the wrapped
/// DEK plus its mint time are the compare-exchange value, keyed by instance id. Payload sealing is AES-256-GCM
/// (Bouncy Castle), framed <c>nonce | tag | ciphertext</c> with the instance id bound in as associated data —
/// identical wire layout to the reference <c>InMemoryInstanceKeyStore</c>.
/// <para>
/// <b>No in-process key cache, by design.</b> Application instances deployed for high availability do not form
/// a cluster — RavenDB does. So RavenDB is the single source of truth for key liveness: every
/// <see cref="Encrypt"/>/<see cref="Decrypt"/> fetches the wrapped DEK, unwraps it under the KEK, does the
/// crypto, then zeroes the transient plaintext DEK. <see cref="Destroy"/> hard-deletes the compare-exchange
/// value, so a shred on any one app instance is immediately effective cluster-wide — a cached DEK on another
/// instance would silently defeat that. The cost is a RavenDB round-trip per crypto call.
/// </para>
/// <para>
/// The RavenDB client's synchronous <c>Operations.Send</c> is used throughout (no sync-over-async bridge). The
/// master KEK is held for the store's lifetime; the per-call unwrapped DEK is zeroed after use (best-effort —
/// it is short-lived and not pinned, since the durable-shred guarantee rests on deleting the wrapped DEK, not
/// on scrubbing a transient copy).
/// </para>
/// <para>
/// <b>Snapshot caveat.</b> <see cref="Destroy"/> deletes the wrapped DEK from the <em>live</em> store, but a
/// RavenDB backup or Raft snapshot taken before the destroy still holds that wrapped DEK, and the KEK is
/// long-lived — so a retained pre-destroy snapshot <em>plus</em> the KEK can reverse a shred. Within the
/// stated threat model the mitigation is operational: keep the KEK in a KMS/HSM and bound snapshot retention.
/// <see cref="RotateKek"/> is the opt-in hardening — after rotating and retiring the old KEK, DEKs captured in
/// older snapshots become permanently unwrappable. (OpenBao's per-instance Transit keys live server-side, so
/// its analogue is server storage snapshots, not a client-held KEK.)
/// </para>
/// </summary>
public sealed class RavenDbInstanceKeyStore : IEnumerableInstanceKeyStore
{
    private const int KeyBytes = 32;
    private const int KekBytes = 32;
    private const int PageSize = 256;
    // Bound the Destroy index-race retry. The wrapped DEK is write-once in normal operation (only RotateKek
    // ever rewrites it, and that is documented as forbidden concurrently with Destroy), so a single delete
    // succeeds; the retries only matter if that invariant is violated, and the cap turns an unbounded spin
    // into a loud failure rather than a hang.
    private const int MaxDestroyAttempts = 32;

    private readonly IDocumentStore _store;
    private byte[] _kek;
    private readonly TimeProvider _time;
    private readonly string _keyPrefix;

    /// <param name="store">A connected RavenDB document store (the shared, durable backing store).</param>
    /// <param name="masterKek">The 32-byte master key that wraps every per-instance data key.</param>
    /// <param name="time">Clock for mint stamps; defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="keyPrefix">Compare-exchange key prefix isolating this store's keys.</param>
    public RavenDbInstanceKeyStore(IDocumentStore store, byte[] masterKek, TimeProvider? time = null, string keyPrefix = "ikey/")
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(masterKek);
        if (masterKek.Length != KekBytes)
        {
            throw new ArgumentException($"master key must be {KekBytes} bytes (AES-256).", nameof(masterKek));
        }

        _store = store;
        // Hold a private copy so a caller mutating its buffer can't change the KEK under us.
        _kek = GC.AllocateArray<byte>(KekBytes, pinned: true);
        masterKek.CopyTo(_kek.AsSpan());
        _time = time ?? TimeProvider.System;
        _keyPrefix = keyPrefix;
    }

    public void Mint(string instanceId)
    {
        // First fetch: if a wrapped DEK already exists, minting is a no-op (preserve the original mint time).
        if (Has(instanceId))
        {
            return;
        }

        byte[] dek = GC.AllocateArray<byte>(KeyBytes, pinned: true);
        try
        {
            RandomNumberGenerator.Fill(dek);
            byte[] wrapped = AesGcmEnvelope.Seal(_kek, dek, Aad(instanceId));
            var value = new WrappedDek { Wrapped = wrapped, MintedAt = _time.GetUtcNow() };

            // index 0 = create-only. If a racing instance already minted, the put is not Successful — that
            // is the idempotent outcome we want (one key per instance, original mint time preserved).
            _store.Operations.Send(new PutCompareExchangeValueOperation<WrappedDek>(Key(instanceId), value, 0));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    public bool Has(string instanceId) => Read(instanceId) is not null;

    public void Destroy(string instanceId)
    {
        // Hard delete the only wrapped copy of the DEK from the shared store, so no app instance (here or
        // elsewhere) can ever unwrap it again — the crypto-shred. The delete is index-guarded: if the value's
        // index moved between the read and the delete (e.g. a concurrent RotateKek re-wrap), the delete is not
        // Successful and would otherwise no-op, leaving the key alive while Destroy returned as if it shredded.
        // Re-read and retry at the fresh index until the value is gone, so a failed shred is never silent.
        for (int attempt = 0; attempt < MaxDestroyAttempts; attempt++)
        {
            CompareExchangeValue<WrappedDek>? current = Read(instanceId);
            if (current is null)
            {
                return; // already shredded or never minted — idempotent.
            }

            CompareExchangeResult<WrappedDek> result = _store.Operations.Send(
                new DeleteCompareExchangeValueOperation<WrappedDek>(Key(instanceId), current.Index));
            if (result.Successful)
            {
                return; // the wrapped DEK is gone cluster-wide.
            }

            // The delete lost the index race (e.g. a concurrent RotateKek re-wrapped). Back off with jitter so
            // racing writers disperse instead of re-colliding in lockstep, then re-read: the next read sees the
            // value gone (idempotent success) or its new index (we retry the delete).
            Backoff(attempt);
        }

        throw new InvalidOperationException(
            $"failed to shred the key for instance '{instanceId}' after {MaxDestroyAttempts} attempts: the " +
            "compare-exchange value kept changing under the delete (is RotateKek running concurrently?). The " +
            "key may still be live — retry the shred.");
    }

    // Exponential backoff capped at ~25ms, fully jittered, so racing writers on one key disperse instead of
    // re-colliding in lockstep (a tight immediate-retry spin thunders and exhausts the attempt budget).
    private static void Backoff(int attempt) =>
        Thread.Sleep(Random.Shared.Next(1, Math.Min(25, 1 << Math.Min(attempt, 4)) + 1));

    public byte[] Encrypt(string instanceId, byte[] plaintext)
    {
        byte[] dek = Unwrap(instanceId);
        try
        {
            return AesGcmEnvelope.Seal(dek, plaintext, Aad(instanceId));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    public byte[] Decrypt(string instanceId, byte[] ciphertext)
    {
        byte[] dek = Unwrap(instanceId);
        try
        {
            return AesGcmEnvelope.Open(dek, ciphertext, Aad(instanceId));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    /// <summary>
    /// Re-wraps every live data key under <paramref name="newKek"/>, then adopts it — the opt-in hardening for
    /// the snapshot caveat in the type remarks. Destroyed instances are intentionally NOT re-wrapped (they have
    /// no live DEK); once the old KEK is retired, their wrapped DEKs in older snapshots can never be unwrapped
    /// again — that is the point. Returns the number of live keys re-wrapped.
    /// <para><b>Run only during a maintenance window with no concurrent Encrypt/Decrypt/Mint/Destroy:</b> a key
    /// minted or read against the wrong KEK across the swap would fail to unwrap. The new KEK only hardens past
    /// shreds once the old KEK is destroyed and snapshots taken under it have aged out.</para>
    /// </summary>
    public int RotateKek(byte[] newKek)
    {
        ArgumentNullException.ThrowIfNull(newKek);
        if (newKek.Length != KekBytes)
        {
            throw new ArgumentException($"master key must be {KekBytes} bytes (AES-256).", nameof(newKek));
        }

        byte[] next = GC.AllocateArray<byte>(KekBytes, pinned: true);
        newKek.CopyTo(next.AsSpan());

        int rewrapped = 0;
        int start = 0;
        while (true)
        {
            Dictionary<string, CompareExchangeValue<WrappedDek>> page = _store.Operations.Send(
                new GetCompareExchangeValuesOperation<WrappedDek>(_keyPrefix, start, PageSize));

            foreach ((string key, CompareExchangeValue<WrappedDek> value) in page)
            {
                if (value.Value is not { } wrapped)
                {
                    continue;
                }

                string instanceId = key[_keyPrefix.Length..];
                byte[] dek = AesGcmEnvelope.Open(_kek, wrapped.Wrapped, Aad(instanceId));
                try
                {
                    var updated = new WrappedDek { Wrapped = AesGcmEnvelope.Seal(next, dek, Aad(instanceId)), MintedAt = wrapped.MintedAt };
                    // Optimistic concurrency on the current index: a racing Destroy bumps it and the put fails,
                    // which is the right outcome — a shredded key must not be resurrected by the re-wrap. Count
                    // only the keys actually re-wrapped, so the returned total never overstates how many live
                    // keys were hardened under the new KEK (a non-Successful put left this one under the old KEK).
                    CompareExchangeResult<WrappedDek> put =
                        _store.Operations.Send(new PutCompareExchangeValueOperation<WrappedDek>(key, updated, value.Index));
                    if (put.Successful)
                    {
                        rewrapped++;
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(dek);
                }
            }

            if (page.Count < PageSize)
            {
                break;
            }

            start += PageSize;
        }

        byte[] old = _kek;
        _kek = next;
        CryptographicOperations.ZeroMemory(old);
        return rewrapped;
    }

    public IReadOnlyCollection<LiveInstance> LiveInstances()
    {
        var live = new List<LiveInstance>();
        int start = 0;
        while (true)
        {
            Dictionary<string, CompareExchangeValue<WrappedDek>> page = _store.Operations.Send(
                new GetCompareExchangeValuesOperation<WrappedDek>(_keyPrefix, start, PageSize));

            foreach ((string key, CompareExchangeValue<WrappedDek> value) in page)
            {
                if (value.Value is { } wrapped)
                {
                    live.Add(new LiveInstance(key[_keyPrefix.Length..], wrapped.MintedAt));
                }
            }

            if (page.Count < PageSize)
            {
                break;
            }

            start += PageSize;
        }

        return live;
    }

    private byte[] Unwrap(string instanceId)
    {
        CompareExchangeValue<WrappedDek>? current = Read(instanceId);
        if (current?.Value is not { } wrapped)
        {
            throw new InvalidOperationException($"no key for instance '{instanceId}' (destroyed or never minted)");
        }

        return AesGcmEnvelope.Open(_kek, wrapped.Wrapped, Aad(instanceId));
    }

    private CompareExchangeValue<WrappedDek>? Read(string instanceId) =>
        _store.Operations.Send(new GetCompareExchangeValueOperation<WrappedDek>(Key(instanceId)));

    private string Key(string instanceId) => _keyPrefix + instanceId;

    private static byte[] Aad(string instanceId) => Encoding.UTF8.GetBytes(instanceId);

    /// <summary>The compare-exchange value: a master-key-wrapped data key plus when the instance was minted.</summary>
    internal sealed class WrappedDek
    {
        public byte[] Wrapped { get; set; } = [];
        public DateTimeOffset MintedAt { get; set; }
    }
}
