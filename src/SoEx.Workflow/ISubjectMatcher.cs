using System.Text;

namespace SoEx.Workflow;

/// <summary>
/// Decides whether a clear-text artifact (a runtime-visible name or a returned result) carries a subject
/// value the termination crypto-shred would not cover. The framework consults a matcher at every guard, so a
/// consumer can replace the default with a stricter detector — a regex for emails/phone numbers/Luhn, an
/// NER model, a denylist — and the guard then catches more than the literal known subject id.
/// </summary>
public interface ISubjectMatcher
{
    /// <summary>
    /// True if <paramref name="text"/> carries a subject value that must not be journaled in clear.
    /// <paramref name="knownSubjectIds"/> are the subject ids the framework already knows for the instance.
    /// </summary>
    bool ContainsSubject(string text, IReadOnlyList<string> knownSubjectIds);

    /// <summary>The UTF-8 byte form, for scanning a serialized result without decoding it to a string first.</summary>
    bool ContainsSubject(ReadOnlySpan<byte> utf8Text, IReadOnlyList<string> knownSubjectIds);
}

/// <summary>
/// The default <see cref="ISubjectMatcher"/>: a case-insensitive substring scan for the subject ids the
/// framework knows for the instance. It is a safety net for <i>known</i> subjects, not general PII
/// detection — plug in your own matcher to catch more.
/// </summary>
public sealed class SubstringSubjectMatcher : ISubjectMatcher
{
    /// <summary>The shared default instance (stateless).</summary>
    public static readonly SubstringSubjectMatcher Default = new();

    public bool ContainsSubject(string text, IReadOnlyList<string> knownSubjectIds)
    {
        foreach (string subjectId in knownSubjectIds)
        {
            if (!string.IsNullOrEmpty(subjectId) && text.Contains(subjectId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public bool ContainsSubject(ReadOnlySpan<byte> utf8Text, IReadOnlyList<string> knownSubjectIds)
    {
        foreach (string subjectId in knownSubjectIds)
        {
            if (!string.IsNullOrEmpty(subjectId) && IndexOf(utf8Text, Encoding.UTF8.GetBytes(subjectId)) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.Length == 0)
        {
            return -1;
        }

        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && FoldAscii(haystack[i + j]) == FoldAscii(needle[j]))
            {
                j++;
            }

            if (j == needle.Length)
            {
                return i;
            }
        }

        return -1;
    }

    // Fold ASCII A–Z to a–z so the byte scan matches the string overload's OrdinalIgnoreCase: a known
    // subject id re-cased in a serialized result must not slip the guard. Full Unicode case folding would
    // need the bytes decoded back to text; the ASCII fold covers the alphabets subject ids actually use,
    // and serializer \uXXXX escaping (a separate evasion) is called out in the guard-scope docs.
    private static byte FoldAscii(byte b) => (byte)(b is >= (byte)'A' and <= (byte)'Z' ? b + 32 : b);
}
