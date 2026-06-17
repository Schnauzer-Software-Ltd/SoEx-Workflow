using System.Security.Cryptography;
using System.Text;

namespace SoEx.Workflow;

/// <summary>
/// Derives a stable, PII-free workflow instance id from business identity — the blessed
/// pattern for the rule that instance ids are journaled in clear and must not carry a
/// subject value. The id is <c>{prefix}-{hex}</c> where the hex is 128 bits of a digest
/// over the flow prefix and the normalized parts, so a caller with nothing but the same business identity
/// (e.g. an org + email from a webhook) re-derives the same id with no lookup and no
/// shared store. Parts are trimmed and lower-cased invariantly before hashing (callers
/// with case-sensitive identifiers should pre-normalize to their own rules); the prefix
/// names the flow and must itself be PII-free.
/// <para>
/// <see cref="For"/> is an unkeyed SHA-256 — deterministic and store-free, but <b>non-secret
/// and confirmable</b>: anyone holding the same identity re-derives it. <see cref="Keyed"/>
/// is HMAC-SHA256 under a shared secret — equally deterministic for callers holding the
/// secret, but <b>not confirmable without it</b>. Choose <see cref="Keyed"/> when a party who
/// knows the identity must still not be able to guess the id.
/// </para>
/// </summary>
public static class DeterministicInstanceId
{
    // 128 bits (32 hex chars): wide enough that the id cannot be brute-forced by collision and
    // removes the trivial confirmation bound a 64-bit truncation carried.
    private const int HexChars = 32;

    /// <summary>
    /// Unkeyed, confirmable derivation. Deterministic and store-free, but a caller holding the same
    /// business identity re-derives (and so can confirm) the id. Use <see cref="Keyed"/> where that matters.
    /// </summary>
    public static string For(string prefix, params string[] parts)
    {
        string normalized = Normalize(prefix, parts);
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Format(prefix, digest);
    }

    /// <summary>
    /// Keyed, unguessable derivation: HMAC-SHA256(<paramref name="secret"/>, normalized parts). Deterministic
    /// for any caller holding the shared <paramref name="secret"/> — the start side and the continue side both
    /// hold it — but an attacker who knows the business identity yet not the secret cannot derive or confirm
    /// the id. Keep the secret out of anything journaled in clear.
    /// </summary>
    public static string Keyed(ReadOnlySpan<byte> secret, string prefix, params string[] parts)
    {
        string normalized = Normalize(prefix, parts);
        byte[] digest = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(normalized));
        return Format(prefix, digest);
    }

    private static string Normalize(string prefix, string[] parts)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        if (parts is not { Length: > 0 })
        {
            throw new ArgumentException("at least one business-identity part is required", nameof(parts));
        }

        // Fold the prefix into the hashed input, not just the human-readable label: otherwise one business
        // identity yields the SAME hex suffix under every flow prefix, so a subject could be correlated across
        // flows by matching suffixes — undercutting Keyed's unguessability. The prefix is the first segment,
        // the parts follow, so distinct flows over one identity get distinct suffixes.
        return string.Join('\n',
            new[] { prefix.Trim().ToLowerInvariant() }.Concat(parts.Select(p => (p ?? "").Trim().ToLowerInvariant())));
    }

    private static string Format(string prefix, byte[] digest) =>
        $"{prefix}-{Convert.ToHexString(digest)[..HexChars].ToLowerInvariant()}";
}
