namespace SoEx.Workflow;

/// <summary>
/// Keeps a throwing step's failure message out of durable backend state in clear. Temporal/DTFx/Elsa/Restate
/// all record an activity/step failure's message in their durable history (and it survives the crypto-shred),
/// so a raw exception message that carries a known subject id would outlive the key that could shred it.
/// Each adapter's step seam consults this before letting a step failure propagate — the in-process analogue
/// of the Zeebe host's incident-message scrub, sharing the same substring guard the framework applies to
/// every other runtime-visible name.
/// </summary>
public static class GovernedStepFailure
{
    /// <summary>The fixed message journaled in place of one that carries a subject id.</summary>
    public const string WithheldMessage = "governed step failed; detail withheld to keep the journal PII-free";

    /// <summary>
    /// True if <paramref name="error"/>'s whole cause chain is free of every subject the framework knows for
    /// the step, so it is safe to journal in clear for diagnosability. False ⇒ the caller must replace it with
    /// <see cref="WithheldMessage"/> before it reaches durable state. Any doubt (a guard that itself throws)
    /// resolves to <c>false</c> — withhold rather than risk a leak.
    /// <para>
    /// The check spans the full chain, not just the top-level <see cref="Exception.Message"/>: a clean outer
    /// exception can wrap a PII-bearing inner one (or an <see cref="AggregateException"/>'s inners), and the
    /// adapters re-throw the original — so the backend journals the whole <see cref="Exception.ToString"/>
    /// (type names, every inner message, the cause chain) into durable history, where it would survive the
    /// crypto-shred. <c>ToString()</c> already walks <c>InnerException</c> / <c>AggregateException.InnerExceptions</c>,
    /// so guarding it covers them all at once.
    /// </para>
    /// </summary>
    public static bool IsJournalSafe(IGovernedStep step, byte[]? ambientContext, Exception error)
    {
        try
        {
            step.GuardVisibleName(error.ToString(), ambientContext);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
