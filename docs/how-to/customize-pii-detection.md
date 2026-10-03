> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# How to customize PII detection

The journal keeps some values in clear text. A guard scans these values for subject ids:

- the instance id
- the event name of each branch of a portable wait, and the event name of a raise through the workflow
  utility
- the raise id, if the gateway has a `GatewaySealGuard`
- the workflow result, and a native business result
- the exception text that the journal or the held log keeps for a failed step

The guard rejects a name, a raise id, or a result that contains a subject id. If exception text contains
a subject id, the framework withholds that text. By default, the guard is a substring scan for the
subject ids that SoEx governs.

The guard does not scan timers, the event names that a native flow waits on through the runtime API, or
an event name that you pass directly to `IWorkflowGateway.RaiseEventAsync`. A portable timer has a
duration and no name. Keep these values PII-free. This scan is a safety net for known subjects only. This guide
makes the guard stricter.

> [!WARNING]
> Keep PII out of names and results in their design. The guard is a backstop only. Derive ids with
> [`DeterministicInstanceId`](trigger-flows-from-outside.md), and name events by kind. Write PII that
> you must keep to your own store in `OnRetaining`. See
> [crypto-shred and erasure](../explanation/crypto-shred-and-erasure.md#what-is-sealed-vs-guarded).

## Add a stricter matcher

An `ISubjectMatcher` does the detection, and you can replace it. Your matcher can find more than the
known subject id. Examples are a regex for email addresses, phone numbers, or Luhn-valid card numbers, an
NER model, or a denylist.

`ISubjectMatcher` has two overloads: a string form and a UTF-8 byte form. The framework scans names in
string form and journaled bytes in byte form.

1. Write a class that implements `ISubjectMatcher`.
2. Implement the string overload with your rule.
3. Implement the byte overload.

The byte overload can decode the bytes and call the string overload.

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

4. Pass the matcher as `subjectMatcher` when you build the governed step.

```csharp
var step = new GovernedStep<IOnboardManager>(endpoint, serializer, idem, keys, index,
    subjectMatcher: new RegexSubjectMatcher());
```

The guard now rejects an instance id, an event name, or a result that matches your rule. This applies on
the portable driver and on the shared native dispatch path.

5. Pass the same matcher as `matcher` to `GatewaySealGuard` and to `TerminationCoordinator`.

`GatewaySealGuard` scans the raise id. `TerminationCoordinator` scans the error text of the held log.
Each one uses the default substring matcher if you do not pass a matcher. The workflow utility always
uses the default substring matcher for the instance id at start and for the event name of a raise.

## Reference

- [The governed core](../reference/governed-core.md) shows where to wire `ISubjectMatcher`.
- [Crypto-shred and erasure](../explanation/crypto-shred-and-erasure.md) tells what is sealed and what
  is guarded. It also gives the reason that the result is guarded only.
