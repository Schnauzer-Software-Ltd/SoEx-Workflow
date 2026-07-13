using System.Globalization;
using System.Text;

namespace SoEx.Workflow;

/// <summary>
/// Canonicalizes a serialized clear-text artifact before the subject scan so a subject id hidden by
/// serializer escaping — a JSON <c>\uXXXX</c> sequence, or a Unicode-decomposed form — cannot slip the
/// substring match that runs over the raw bytes. The raw byte scan still runs; this is an additional pass
/// over the decoded/normalized text, so it only ever catches more, never less. (The default matcher's
/// byte scan misses a <c>\uXXXX</c>-escaped subject — pinned as a known limitation until this pass.)
/// </summary>
internal static class JournalCanonicalization
{
    /// <summary>
    /// Throws if the canonical form of <paramref name="serialized"/> carries a known subject; returns the
    /// bytes unchanged otherwise. Best-effort by design: bytes that are not text (a binary serializer)
    /// canonicalize to nothing the matcher can catch and simply pass this pass — the raw byte scan in
    /// <see cref="RuntimeVisibleName.RequireBytesFree"/> remains the guarantee for those.
    /// </summary>
    public static byte[] RequireCanonicalFree(
        byte[] serialized, IEnumerable<string> subjectIds, string what, ISubjectMatcher? matcher = null)
    {
        IReadOnlyList<string> ids = subjectIds as IReadOnlyList<string> ?? subjectIds.ToList();
        string canonical = Canonicalize(serialized);
        if ((matcher ?? SubstringSubjectMatcher.Default).ContainsSubject(canonical, ids))
        {
            throw new InvalidOperationException(
                $"{what} is journaled in clear and survives the termination shred, so it must be PII-free; a canonicalized form carries a subject id");
        }

        return serialized;
    }

    private static string Canonicalize(byte[] serialized) =>
        UnescapeUnicode(Encoding.UTF8.GetString(serialized)).Normalize(NormalizationForm.FormKC);

    // Decodes \uXXXX escapes (the JSON serializer's way of writing a non-ASCII — or a deliberately obscured —
    // subject id) back to the characters they denote, so the scan sees "abc" where the raw bytes held
    // "abc". Everything else is passed through verbatim; a malformed escape is left as-is.
    private static string UnescapeUnicode(string text)
    {
        if (!text.Contains("\\u", StringComparison.Ordinal))
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 5 < text.Length && text[i + 1] == 'u'
                && ushort.TryParse(text.AsSpan(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort code))
            {
                sb.Append((char)code);
                i += 5;
            }
            else
            {
                sb.Append(text[i]);
            }
        }

        return sb.ToString();
    }
}
