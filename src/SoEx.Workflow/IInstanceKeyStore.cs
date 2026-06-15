namespace SoEx.Workflow;

/// <summary>
/// The runtime-agnostic per-instance key store. A key is minted at instance start
/// (unconditionally, independent of whether a subject is known), used to encrypt
/// everything the instance persists, carried across <c>ContinueAsNew</c>, and
/// hard-deleted at termination — destroying it renders the instance's persisted
/// payload (including replay history) cryptographically unrecoverable (crypto-shred).
/// </summary>
public interface IInstanceKeyStore
{
    /// <summary>Mints the instance key. Idempotent — exactly one key per instance, start-anchored.</summary>
    void Mint(string instanceId);

    bool Has(string instanceId);

    /// <summary>Hard-deletes the key. After this, <see cref="Decrypt"/> for the instance can never succeed.</summary>
    void Destroy(string instanceId);

    /// <summary>Encrypts a payload with the instance key. Throws if no key is present.</summary>
    byte[] Encrypt(string instanceId, byte[] plaintext);

    /// <summary>Decrypts a payload with the instance key. Throws if the key is absent (destroyed/never minted) or the data is tampered.</summary>
    byte[] Decrypt(string instanceId, byte[] ciphertext);
}

/// <summary>
/// One un-shredded instance and the moment its key was first minted — i.e. when the instance
/// first existed. The <see cref="MintedAt"/> stamp is what an erasure sweep ages against.
/// </summary>
public readonly record struct LiveInstance(string InstanceId, DateTimeOffset MintedAt);

/// <summary>
/// Optional capability for a key store that can enumerate the instances it still holds keys
/// for. That set is exactly the un-terminated ("live") instances — a destroyed key is the
/// termination — so it is precisely what an abandoned-instance erasure sweep needs to find
/// instances whose termination hook never ran. A store that cannot enumerate (e.g. a
/// write-through HSM that only answers point lookups) simply does not implement this; the
/// sweep is then unavailable rather than wrong.
/// </summary>
public interface IEnumerableInstanceKeyStore : IInstanceKeyStore
{
    /// <summary>A snapshot of every instance with a live key, each stamped with its mint time.</summary>
    IReadOnlyCollection<LiveInstance> LiveInstances();
}
