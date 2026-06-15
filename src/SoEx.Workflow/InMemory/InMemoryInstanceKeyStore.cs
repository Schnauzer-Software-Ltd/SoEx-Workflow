using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace SoEx.Workflow.InMemory;

/// <summary>
/// In-memory <see cref="IInstanceKeyStore"/>. Holds a per-instance AES-256-GCM key
/// in process memory; destroying it zeroes the key bytes in place (the array is pinned, so no
/// stray GC copies survive) before dropping them — unrecoverable even from a heap dump.
/// Ciphertext is framed as <c>nonce | tag | ciphertext</c>, with the instance id bound in as
/// associated data — an envelope only decrypts for the instance it was sealed for, so even a
/// key store that wrongly hands two instances one key cannot replay an envelope across them.
/// <para>
/// In-process only: a single map of keys, so it backs durable crypto-shred only when the
/// instance's client, orchestrator, and step workers share one process. Production needs a
/// durable, shared key store (DB/KMS/HSM); this is the reference implementation and the
/// Tier-1 test default. Concurrency-safe (a <see cref="ConcurrentDictionary{TKey,TValue}"/>).
/// </para>
/// </summary>
public sealed class InMemoryInstanceKeyStore(TimeProvider? time = null) : IEnumerableInstanceKeyStore
{
    private const int KeyBytes = 32;
    private static readonly int NonceBytes = AesGcm.NonceByteSizes.MaxSize;
    private static readonly int TagBytes = AesGcm.TagByteSizes.MaxSize;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, byte[]> _keys = new();

    // Serializes the in-place key zeroing in Destroy against the crypto ops that read the same key array.
    // Without it, Destroy can zero the array a concurrent Encrypt/Decrypt is mid-use, sealing under an
    // all-zero (known) key — a force-terminate racing a final write. Under the gate the two are ordered:
    // the crypto op either completes under the live key or, if Destroy won, throws key-gone — never zeros.
    private readonly object _cryptoGate = new();

    // When each live key was first minted — the instance's "exists since", aged against by the
    // erasure sweep. Kept in lock-step with _keys: an entry appears on first Mint and is removed
    // on Destroy, so the two maps always describe the same set of live instances.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _mintedAt = new();

    public void Mint(string instanceId) =>
        _keys.GetOrAdd(instanceId, _ =>
        {
            // First mint for this id — stamp when the instance came into existence. GetOrAdd's
            // factory runs only on insert, so a redelivered Mint never overwrites the original time.
            _mintedAt[instanceId] = _time.GetUtcNow();

            // Pinned, so the GC never relocates the array and leaves stale copies of the key
            // behind on the heap — Destroy's zeroing then scrubs the only managed copy.
            byte[] key = GC.AllocateArray<byte>(KeyBytes, pinned: true);
            RandomNumberGenerator.Fill(key);
            return key;
        });

    public bool Has(string instanceId) => _keys.ContainsKey(instanceId);

    public IReadOnlyCollection<LiveInstance> LiveInstances() =>
        _mintedAt.Select(kv => new LiveInstance(kv.Key, kv.Value)).ToList();

    public void Destroy(string instanceId)
    {
        lock (_cryptoGate)
        {
            _mintedAt.TryRemove(instanceId, out _);
            if (_keys.TryRemove(instanceId, out byte[]? key))
            {
                // Scrub the key material rather than leaving it for the GC: after the shred the
                // bytes must not be recoverable from a heap dump either.
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    public byte[] Encrypt(string instanceId, byte[] plaintext)
    {
        lock (_cryptoGate)
        {
            byte[] key = KeyFor(instanceId);
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceBytes);
            byte[] tag = new byte[TagBytes];
            byte[] ciphertext = new byte[plaintext.Length];

            using var aes = new AesGcm(key, TagBytes);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(instanceId));

            return [.. nonce, .. tag, .. ciphertext];
        }
    }

    public byte[] Decrypt(string instanceId, byte[] framed)
    {
        lock (_cryptoGate)
        {
            byte[] key = KeyFor(instanceId);

            ReadOnlySpan<byte> span = framed;
            ReadOnlySpan<byte> nonce = span[..NonceBytes];
            ReadOnlySpan<byte> tag = span.Slice(NonceBytes, TagBytes);
            ReadOnlySpan<byte> ciphertext = span[(NonceBytes + TagBytes)..];
            byte[] plaintext = new byte[ciphertext.Length];

            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(instanceId));

            return plaintext;
        }
    }

    private byte[] KeyFor(string instanceId) =>
        _keys.TryGetValue(instanceId, out byte[]? key)
            ? key
            : throw new InvalidOperationException($"no key for instance '{instanceId}' (destroyed or never minted)");
}
