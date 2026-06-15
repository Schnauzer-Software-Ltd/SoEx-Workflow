> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# How to customize PII detection

SoEx journals two things in clear (the instance id and event names, and the workflow result) and
guards them by rejecting any that carry a subject id. By default that guard is a substring scan for the
subject ids SoEx already governs, which makes it a safety net for known subjects rather than general
PII detection. This guide shows how to make it stricter.

> The guard is a backstop, not a primary defense. Keep names and results PII-free by construction:
> derive ids with [`DeterministicInstanceId`](trigger-flows-from-outside.md), name events by kind, and
> write must-retain PII outward in `OnRetaining`. See
> [crypto-shred and erasure](../explanation/crypto-shred-and-erasure.md#what-is-sealed-vs-guarded).

## Plug in a stricter matcher

The detection is a pluggable `ISubjectMatcher`. Supply your own to catch more than the literal known
subject id: a regex for emails, phone numbers, or Luhn-valid card numbers, an NER model, or a
denylist.

The interface has two overloads, a string form and a UTF-8 byte form, because the framework scans both
names and journaled bytes. Implement both; the byte overload can decode and delegate:

```csharp
sealed class RegexSubjectMatcher : ISubjectMatcher
{
    static readonly Regex Email = new(@"[^@\s]+@[^@\s]+\.[^@\s]+", RegexOptions.Compiled);

    // return true if `text` carries something that must never be journaled in clear
    public bool ContainsSubject(string text, IReadOnlyList<string> knownSubjectIds) =>
        knownSubjectIds.Any(text.Contains) || Email.IsMatch(text);

    public bool ContainsSubject(ReadOnlySpan<byte> utf8Text, IReadOnlyList<string> knownSubjectIds) =>
        ContainsSubject(Encoding.UTF8.GetString(utf8Text), knownSubjectIds);
}
```

Pass it where you build the governed step:

```csharp
var step = new GovernedStep<IOnboardSteps>(endpoint, serializer, idem, keys, index,
    subjectMatcher: new RegexSubjectMatcher());
```

Now the guard rejects an instance id or result that matches your stricter rule, on both the portable
driver and the shared native dispatch path. Timers carry no guarded id.

## Reference

- [The governed core](../reference/governed-core.md) — where `ISubjectMatcher` is wired.
- [Crypto-shred and erasure](../explanation/crypto-shred-and-erasure.md) — what is sealed vs guarded,
  and why the result is only guarded.
