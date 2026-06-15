using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using VaultSharp;
using VaultSharp.Core;
using VaultSharp.V1.AuthMethods;
using VaultSharp.V1.AuthMethods.Token;
using VaultSharp.V1.Commons;
using VaultSharp.V1.SecretsEngines.Transit;

namespace SoEx.Workflow.Keys.OpenBao;

/// <summary>
/// A durable, shared <see cref="IInstanceKeyStore"/> backed by OpenBao's Transit secrets engine (OpenBao
/// speaks the Vault HTTP API, so VaultSharp is the client). One Transit key is created per instance; the key
/// material <b>never leaves the server</b> — <see cref="Encrypt"/>/<see cref="Decrypt"/> are server-side
/// operations — and <see cref="Destroy"/> deletes the key, after which every envelope sealed under it is
/// permanently undecryptable (crypto-shred). Because each instance has its own key, an envelope cannot be
/// replayed across instances; that per-key isolation is the equivalent of the in-memory store's AAD binding.
/// <para>
/// The interface is synchronous and is called on the seal hot path; VaultSharp is async-only, so each call
/// blocks on its task via a thread-pool hop (<c>Task.Run(...).GetAwaiter().GetResult()</c>) — safe because the
/// seal path runs under no single-threaded <c>SynchronizationContext</c> (generic host / console / worker). Do
/// not call from a UI or legacy-ASP.NET sync context. Encrypt/Decrypt are inherently per-call round-trips to
/// OpenBao — that latency is the trade for the key never leaving the server.
/// </para>
/// <para>
/// <b>Snapshot caveat.</b> <see cref="Destroy"/> deletes the Transit key on the server, but an OpenBao
/// <em>storage snapshot</em> (Raft/Consul backup) taken before the destroy still contains that key — restoring
/// such a snapshot would resurrect it, reversing the shred. The mitigation is operational and server-side
/// (bound snapshot retention; protect/rotate the unseal keys), the OpenBao analogue of the RavenDB store's
/// KEK-snapshot caveat — here there is no client-held KEK to rotate, since the key material is server-side.
/// </para>
/// </summary>
public sealed class OpenBaoInstanceKeyStore : IEnumerableInstanceKeyStore
{
    // Transit key names allow only [a-zA-Z0-9_.-]; instance ids do not. Hex-encode the id (reversible,
    // in-charset) behind a fixed prefix so LiveInstances can recover the original id.
    private const string NamePrefix = "inst-";

    private readonly IVaultClient _client;
    private readonly string _mountPoint;

    /// <param name="address">The OpenBao server address, e.g. <c>https://openbao:8200</c>.</param>
    /// <param name="token">A token with rights on the Transit mount.</param>
    /// <param name="mountPoint">The Transit engine mount point (default <c>transit</c>).</param>
    /// <remarks>
    /// <b>Transport security is the caller's responsibility.</b> Transit does the crypto server-side, so unlike
    /// the in-memory and RavenDB stores this one sends the <em>plaintext</em> (base64) to the server on every
    /// <see cref="Encrypt"/> and receives plaintext back on every <see cref="Decrypt"/>. The key material never
    /// leaves the server, but the protected data does — so a plain <c>http://</c> address ships that data, and
    /// the token, in clear. Use an <c>https://</c> address in production (loopback <c>http</c> is fine for dev),
    /// or inject a TLS/mTLS-configured client through the <see cref="OpenBaoInstanceKeyStore(IVaultClient,string)"/>
    /// overload when you need a custom handler, client certificate, or pinned CA.
    /// </remarks>
    public OpenBaoInstanceKeyStore(string address, string token, string mountPoint = "transit")
        : this(BuildClient(address, token), mountPoint)
    {
    }

    /// <param name="client">A pre-built Vault/OpenBao client.</param>
    /// <param name="mountPoint">The Transit engine mount point (default <c>transit</c>).</param>
    public OpenBaoInstanceKeyStore(IVaultClient client, string mountPoint = "transit")
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _mountPoint = mountPoint;
    }

    private static IVaultClient BuildClient(string address, string token)
    {
        IAuthMethodInfo auth = new TokenAuthMethodInfo(token);
        return new VaultClient(new VaultClientSettings(address, auth));
    }

    public void Mint(string instanceId)
    {
        // Idempotent, start-anchored: the key's creation_time is the mint time and is set once. Re-create is a
        // server no-op, but we also guard with a read so a build that rejects re-create can't break minting.
        if (Has(instanceId))
        {
            return;
        }

        Block(() => _client.V1.Secrets.Transit.CreateEncryptionKeyAsync(
            Name(instanceId),
            new CreateKeyRequestOptions { Type = TransitKeyType.aes256_gcm96 },
            _mountPoint));
    }

    public bool Has(string instanceId)
    {
        try
        {
            Block(() => _client.V1.Secrets.Transit.ReadEncryptionKeyAsync(Name(instanceId), _mountPoint));
            return true;
        }
        catch (VaultApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public void Destroy(string instanceId)
    {
        if (!Has(instanceId))
        {
            return; // already shredded or never minted — idempotent.
        }

        // A Transit key cannot be deleted until its config allows it; flip the flag, then delete.
        Block(() => _client.V1.Secrets.Transit.UpdateEncryptionKeyConfigAsync(
            Name(instanceId),
            new UpdateKeyRequestOptions { DeletionAllowed = true },
            _mountPoint));
        Block(() => _client.V1.Secrets.Transit.DeleteEncryptionKeyAsync(Name(instanceId), _mountPoint));
    }

    public byte[] Encrypt(string instanceId, byte[] plaintext)
    {
        try
        {
            Secret<EncryptionResponse> resp = Block(() => _client.V1.Secrets.Transit.EncryptAsync(
                Name(instanceId),
                new EncryptRequestOptions { Base64EncodedPlainText = Convert.ToBase64String(plaintext) },
                _mountPoint));

            // The server's "vault:v1:<b64>" envelope, carried verbatim as the stored ciphertext bytes.
            return Encoding.UTF8.GetBytes(resp.Data.CipherText);
        }
        catch (VaultApiException e) when (e.HttpStatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            // For a valid plaintext the only expected server failure is the key being absent (destroyed/never minted).
            throw NoKey(instanceId, e);
        }
    }

    public byte[] Decrypt(string instanceId, byte[] ciphertext)
    {
        try
        {
            Secret<DecryptionResponse> resp = Block(() => _client.V1.Secrets.Transit.DecryptAsync(
                Name(instanceId),
                new DecryptRequestOptions { CipherText = Encoding.UTF8.GetString(ciphertext) },
                _mountPoint));

            return Convert.FromBase64String(resp.Data.Base64EncodedPlainText);
        }
        catch (VaultApiException e) when (e.HttpStatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            // OpenBao reports the SAME "encryption key not found" for a wrong-key / corrupt-ciphertext decrypt as
            // for a genuinely absent key, so the server response cannot tell them apart. Disambiguate by existence:
            // a PRESENT key that cannot open this envelope is a crypto rejection (wrong instance / forged / corrupt);
            // an ABSENT key is no-key. Conflating them reported "no key" for a key that exists.
            throw Has(instanceId)
                ? new CryptographicException($"the envelope did not decrypt under instance '{instanceId}'s key", e)
                : NoKey(instanceId, e);
        }
    }

    public IReadOnlyCollection<LiveInstance> LiveInstances()
    {
        IEnumerable<string> names;
        try
        {
            Secret<VaultSharp.V1.Commons.ListInfo> list =
                Block(() => _client.V1.Secrets.Transit.ReadAllEncryptionKeysAsync(_mountPoint));
            names = list.Data.Keys;
        }
        catch (VaultApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return []; // no keys minted yet — LIST 404s.
        }

        var live = new List<LiveInstance>();
        foreach (string name in names)
        {
            if (!name.StartsWith(NamePrefix, StringComparison.Ordinal))
            {
                continue; // not ours (shared mount) — ignore.
            }

            Secret<EncryptionKeyInfo> info =
                Block(() => _client.V1.Secrets.Transit.ReadEncryptionKeyAsync(name, _mountPoint));
            live.Add(new LiveInstance(InstanceIdFromName(name), MintedAt(info.Data)));
        }

        return live;
    }

    // --- Transit key creation_time → MintedAt -------------------------------------------------------------

    // For a symmetric (aes256_gcm96) key, Transit's "keys" map is version → creation time. Across Vault/OpenBao
    // versions that value has been a Unix-seconds number, an RFC3339 string, or a nested object carrying
    // "creation_time"; parse all three. The mint time is the earliest version's stamp.
    private static DateTimeOffset MintedAt(EncryptionKeyInfo info)
    {
        DateTimeOffset earliest = DateTimeOffset.MaxValue;
        foreach (object? value in info.Keys.Values)
        {
            if (TryParseCreationTime(value, out DateTimeOffset when) && when < earliest)
            {
                earliest = when;
            }
        }

        // If the stamp is unreadable, fall back to "now" rather than MinValue: an unparseable time must not make
        // the sweep treat a live instance as infinitely aged and wrongly shred it.
        return earliest == DateTimeOffset.MaxValue ? DateTimeOffset.UtcNow : earliest;
    }

    // The "keys" map value is whatever the JSON deserializer produced for the version's creation time — a
    // boxed long (Newtonsoft) or a JsonElement (System.Text.Json), and historically a Unix-seconds number, an
    // RFC3339 string, or a nested object carrying "creation_time". Go through ToString() so it works whichever
    // serializer VaultSharp uses: a number renders as its digits, a string renders as its text.
    private static bool TryParseCreationTime(object? value, out DateTimeOffset when)
    {
        when = default;
        string? text = value?.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long unix))
        {
            when = DateTimeOffset.FromUnixTimeSeconds(unix);
            return true;
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
        {
            when = parsed;
            return true;
        }

        // Nested object form: reach for the RFC3339 value following "creation_time".
        if (text.Contains("creation_time", StringComparison.Ordinal))
        {
            string tail = text[text.IndexOf("creation_time", StringComparison.Ordinal)..];
            if (DateTimeOffset.TryParse(
                    tail.Split('"').FirstOrDefault(p => p.Contains('T')) ?? string.Empty,
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset nested))
            {
                when = nested;
                return true;
            }
        }

        return false;
    }

    // --- helpers ------------------------------------------------------------------------------------------

    private static string Name(string instanceId) =>
        NamePrefix + Convert.ToHexStringLower(Encoding.UTF8.GetBytes(instanceId));

    private static string InstanceIdFromName(string name) =>
        Encoding.UTF8.GetString(Convert.FromHexString(name[NamePrefix.Length..]));

    private static InvalidOperationException NoKey(string instanceId, Exception inner) =>
        new($"no key for instance '{instanceId}' (destroyed or never minted)", inner);

    private static T Block<T>(Func<Task<T>> f) => Task.Run(f).GetAwaiter().GetResult();

    private static void Block(Func<Task> f) => Task.Run(f).GetAwaiter().GetResult();
}
