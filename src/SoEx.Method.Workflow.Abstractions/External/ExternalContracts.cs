namespace SoEx.Method.Workflow.External;

/// <summary>
/// The <b>external</b> face of the Workflow utility — what the host/ingress layer calls as a SoEx system
/// client (the example's web endpoints): the request-driven right-to-erasure and the abandoned-instance
/// backstop sweep. A distinct contract type from the utility's <see cref="SoEx.Method.Workflow.SubSystem.IWorkflowUtility"/> face so the system
/// client and the peer-component proxy never alias on one type (SoEx resolves a contract to one channel per
/// container). The utility owns the erasure logic behind every operation; the host owns the cadence.
/// <para>
/// Right-to-erasure is a single model: <see cref="RequestEraseAsync"/> admits a request durably and returns at
/// once; <see cref="DrainEraseRequestsAsync"/> (a scheduled maintenance pass) does the crypto-shred. Erasure is
/// a statutory-deadline job, not a synchronous SLA, so there is no blocking variant.
/// </para>
/// </summary>
public interface IWorkflowUtility
{
    /// <summary>Right-to-erasure, admitted durably: resolves the subject to its instances and records that
    /// PII-free set, returning a request id at once — without shredding. A later <see cref="DrainEraseRequestsAsync"/>
    /// pass does the synchronous, per-manager shred. The caller is not blocked; loss before the drain only delays
    /// the start (the sweep and deadline review backstop it). Idempotent on the subject. Requires an
    /// <c>IPendingErasureRequests</c> store; throws if none is wired.</summary>
    Task<string> RequestEraseAsync(string subject);

    /// <summary>One pass: drains admitted erasure requests, driving each instance to crypto-shred through its
    /// owning manager's termination, then marking it drained. Returns the number drained. Schedule it like the
    /// other passes, within your statutory deadline. Requires an <c>IPendingErasureRequests</c> store; throws if
    /// none is wired.</summary>
    Task<int> DrainEraseRequestsAsync();

    /// <summary>The request-independent backstop, one pass: force-terminates instances abandoned before their
    /// termination hook ran (older than <paramref name="olderThanSeconds"/>). The utility owns the sweep logic;
    /// the host owns the cadence (calls this on a timer). Returns the number of aged instances driven.
    /// <paramref name="olderThanSeconds"/> must exceed the longest legitimate flow so live work is untouched.</summary>
    Task<int> SweepAbandonedAsync(int olderThanSeconds);

    /// <summary>One pass: re-drives every quarantined (held) instance — an audited retry of its termination
    /// extraction. Returns the number that re-drove to a clean termination this pass. The host owns the cadence.</summary>
    Task<int> ReDriveHeldAsync();

    /// <summary>One pass: re-evaluates open erasure requests against the statutory clock, force-terminating
    /// instances left to complete naturally that are now within <paramref name="escalateWithinSeconds"/> of
    /// their deadline. Returns the number force-terminated. The host owns the cadence.</summary>
    Task<int> ReviewDeadlinesAsync(int escalateWithinSeconds);
}
