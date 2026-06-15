using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace SoEx.Workflow.Keys.RavenDB;

/// <summary>
/// AES-256-GCM sealing via Bouncy Castle, framed <c>nonce(12) | tag(16) | ciphertext</c> with caller-supplied
/// associated data (AAD) — byte-compatible with the framework's reference <c>InMemoryInstanceKeyStore</c>.
/// Used for both layers of the RavenDB store: wrapping the data key under the master key, and sealing the
/// payload under the data key. Bouncy Castle emits <c>ciphertext || tag</c>; this helper re-frames to put the
/// tag ahead of the ciphertext (and reassembles on open), so the on-the-wire layout matches the BCL form.
/// </summary>
internal static class AesGcmEnvelope
{
    private const int NonceBytes = 12;
    private const int TagBits = 128;
    private const int TagBytes = TagBits / 8;

    public static byte[] Seal(byte[] key, byte[] plaintext, byte[] aad)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceBytes);

        var cipher = new GcmBlockCipher(new AesEngine());
        // AAD via the 4-arg AeadParameters ctor — do NOT also call ProcessAadBytes (that double-feeds it).
        cipher.Init(forEncryption: true, new AeadParameters(new KeyParameter(key), TagBits, nonce, aad));

        byte[] outBuf = new byte[cipher.GetOutputSize(plaintext.Length)]; // ciphertext || tag
        int len = cipher.ProcessBytes(plaintext, 0, plaintext.Length, outBuf, 0);
        cipher.DoFinal(outBuf, len);

        // Re-frame ciphertext||tag → nonce | tag | ciphertext.
        ReadOnlySpan<byte> produced = outBuf;
        ReadOnlySpan<byte> ciphertext = produced[..plaintext.Length];
        ReadOnlySpan<byte> tag = produced.Slice(plaintext.Length, TagBytes);
        return [.. nonce, .. tag, .. ciphertext];
    }

    public static byte[] Open(byte[] key, byte[] framed, byte[] aad)
    {
        ReadOnlySpan<byte> span = framed;
        byte[] nonce = span[..NonceBytes].ToArray();
        ReadOnlySpan<byte> tag = span.Slice(NonceBytes, TagBytes);
        ReadOnlySpan<byte> ciphertext = span[(NonceBytes + TagBytes)..];

        // Bouncy Castle wants ciphertext||tag back together for verification on DoFinal.
        byte[] input = new byte[ciphertext.Length + TagBytes];
        ciphertext.CopyTo(input);
        tag.CopyTo(input.AsSpan(ciphertext.Length));

        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(forEncryption: false, new AeadParameters(new KeyParameter(key), TagBits, nonce, aad));

        byte[] outBuf = new byte[cipher.GetOutputSize(input.Length)]; // = ciphertext length
        int len = cipher.ProcessBytes(input, 0, input.Length, outBuf, 0);
        cipher.DoFinal(outBuf, len); // throws InvalidCipherTextException on tag/AAD mismatch
        return outBuf;
    }
}
