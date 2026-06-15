using System.Security.Cryptography;
using System.Text;

namespace SoEx.Workflow;

/// <summary>
/// Derives the PII-free <em>token</em> a durable <see cref="ISubjectIndex"/> uses as a subject's at-rest lookup
/// key, so the plaintext subject id (e.g. an email) is never stored as a row/document key. The token is stable
/// and deterministic — <c>InstancesFor</c> tokenizes its query and matches, and the same subject tokenizes the
/// same way across processes and restarts — but one-way, so it carries no recoverable PII (it is PII-free
/// residue, exactly like a <see cref="DeterministicInstanceId"/>).
/// <para>
/// The plaintext subject itself is NOT sealed here — the durable index seals it under the per-instance
/// crypto-shred key (so it is rendered unrecoverable at the instance's termination, like all other instance data).
/// This type only governs the lookup token.
/// </para>
/// </summary>
public interface ISubjectProtector
{
    /// <summary>A stable, deterministic, one-way token for the subject id — the at-rest lookup key.</summary>
    string Tokenize(string subjectId);
}

/// <summary>
/// Marks a protector whose "token" is (or may be) the plaintext subject — an identity/pass-through tokenizer
/// safe only where nothing is written to disk. Durable governance stores (the subject index, the erasure-request
/// registry) reject any <see cref="ISubjectProtector"/> carrying this marker, so a pass-through tokenizer cannot
/// silently put recoverable subjects at rest. In-memory stores (RAM, no at-rest residue) accept it.
/// </summary>
public interface IPlaintextSubjectProtector : ISubjectProtector;

/// <summary>Guards a durable governance store against a pass-through tokenizer that would put plaintext at rest.</summary>
public static class DurableSubjectProtector
{
    /// <summary>
    /// Returns <paramref name="protector"/> if it is safe for a durable store, else throws. Rejects a
    /// <see cref="IPlaintextSubjectProtector"/> (e.g. <see cref="NullSubjectProtector"/>), which would persist a
    /// recoverable subject id — defeating the at-rest protection the durable store exists to provide.
    /// </summary>
    /// <param name="store">What is being constructed, for the error message (e.g. "a durable subject index").</param>
    public static ISubjectProtector Require(ISubjectProtector protector, string store)
    {
        ArgumentNullException.ThrowIfNull(protector);
        if (protector is IPlaintextSubjectProtector)
        {
            throw new ArgumentException(
                $"{store} must not use a plaintext (pass-through) ISubjectProtector such as NullSubjectProtector — it would " +
                "persist recoverable subject ids at rest, the very thing the durable store exists to prevent. Use HmacSubjectProtector.",
                nameof(protector));
        }

        return protector;
    }
}

/// <summary>
/// The identity tokenizer: the token is the plaintext subject itself. The default for the in-memory index (RAM
/// holds no at-rest residue) and for back-compat. Never use it with a durable index or erasure-request registry,
/// whose whole purpose is to keep plaintext off disk — they reject it (it is an <see cref="IPlaintextSubjectProtector"/>).
/// </summary>
public sealed class NullSubjectProtector : IPlaintextSubjectProtector
{
    /// <summary>The shared stateless instance.</summary>
    public static readonly NullSubjectProtector Instance = new();

    public string Tokenize(string subjectId) => subjectId;
}

/// <summary>
/// The production tokenizer: an HMAC-SHA256 of the subject id under a deployment secret, hex-encoded. The token
/// is deterministic (lookups work cross-process and across a restart) and one-way. If the deployment secret
/// leaks an attacker can at most <em>confirm a guessed</em> subject against a token — never recover a subject
/// from one, and never recover the plaintext the index sealed under the per-instance key.
/// </summary>
public sealed class HmacSubjectProtector : ISubjectProtector
{
    private readonly byte[] _tokenKey;

    /// <param name="secret">A stable deployment secret (at least 16 bytes). If it is lost or rotated, existing
    /// tokens no longer match — treat it as durable lookup-key material.</param>
    public HmacSubjectProtector(ReadOnlySpan<byte> secret)
    {
        if (secret.Length < 16)
        {
            throw new ArgumentException("the subject-protector secret must be at least 16 bytes", nameof(secret));
        }

        byte[] tokenKey = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, tokenKey, salt: default, info: Encoding.UTF8.GetBytes("soex-workflow:subject-token"));
        _tokenKey = tokenKey;
    }

    public string Tokenize(string subjectId)
    {
        ArgumentNullException.ThrowIfNull(subjectId);
        return Convert.ToHexString(HMACSHA256.HashData(_tokenKey, Encoding.UTF8.GetBytes(subjectId)));
    }
}
