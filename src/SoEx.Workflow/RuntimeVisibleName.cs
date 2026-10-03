namespace SoEx.Workflow;

/// <summary>
/// Guards the clear-text values the shred does <b>not</b> cover from carrying a subject value: instance
/// ids, the event names of a portable wait and of a utility raise, a gateway raise id (when a
/// <see cref="GatewaySealGuard"/> is wired), the returned workflow result, and held-log error text. A
/// timer has a duration and no name, so there is nothing to guard. The subject lives only in the
/// (prunable) subject index and the encrypted payload, so a copy in one of these survives the termination
/// crypto-shred. Detection is delegated to an <see cref="ISubjectMatcher"/> — the default
/// <see cref="SubstringSubjectMatcher"/> scans for the known subject ids, but a consumer can plug in a
/// stricter detector, so this is a configurable chokepoint rather than substring-only by construction.
/// </summary>
public static class RuntimeVisibleName
{
    /// <summary>Returns <paramref name="name"/> if the matcher finds no subject in it; otherwise throws.</summary>
    public static string Require(string name, IEnumerable<string> subjectIds, ISubjectMatcher? matcher = null)
    {
        IReadOnlyList<string> ids = subjectIds as IReadOnlyList<string> ?? subjectIds.ToList();
        if ((matcher ?? SubstringSubjectMatcher.Default).ContainsSubject(name, ids))
        {
            throw new ArgumentException(
                "runtime-visible name must be PII-free; it carries a subject id", nameof(name));
        }

        return name;
    }

    /// <summary>
    /// Returns <paramref name="serialized"/> if the matcher finds no subject in it; otherwise throws.
    /// Used for clear-text bytes that escape the shred (notably the returned workflow result):
    /// they must be PII-free for the subjects the framework governs. <paramref name="what"/> names
    /// the artifact in the error.
    /// </summary>
    public static byte[] RequireBytesFree(
        byte[] serialized, IEnumerable<string> subjectIds, string what, ISubjectMatcher? matcher = null)
    {
        IReadOnlyList<string> ids = subjectIds as IReadOnlyList<string> ?? subjectIds.ToList();
        if ((matcher ?? SubstringSubjectMatcher.Default).ContainsSubject(serialized, ids))
        {
            throw new InvalidOperationException(
                $"{what} is journaled in clear and survives the termination shred, so it must be PII-free; it carries a subject id");
        }

        return serialized;
    }
}
