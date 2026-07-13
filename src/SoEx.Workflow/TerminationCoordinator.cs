namespace SoEx.Workflow;

public enum TerminationOutcome
{
    Terminated,
    Held,
}

/// <summary>
/// Drives the termination lifecycle the three contracts encode:
/// <c>OnRetaining</c> (while the key is live) → destroy the key (crypto-shred) →
/// prune the subject index → <c>OnTerminated</c>; or, when <c>OnRetaining</c> fails
/// past the retry boundary, → <c>OnRetentionHeld</c> with the key <b>retained</b>
/// (non-final). A held instance recovers via the audited <see cref="ReDriveAsync"/>.
/// </summary>
public sealed class TerminationCoordinator(
    IInstanceKeyStore keys, ISubjectIndex index, int maxRetainingAttempts = 3, Func<int, Task>? backoffDelay = null,
    IHeldInstanceRegistry? heldRegistry = null, ISubjectMatcher? matcher = null, WorkflowMetrics? metrics = null,
    IErasureTombstone? tombstone = null)
{
    public async Task<TerminationOutcome> TerminateAsync(
        string instanceId, IErasureEvent contracts, IdempotencyKey idempotencyKey, TerminationTrigger trigger)
    {
        // Idempotent re-entry: if the key is already gone, a prior termination completed OnRetaining +
        // crypto-shred. Re-running would re-extract against an unreadable (shredded) payload, so treat
        // a redelivered termination as the success it already is. A held instance still has its key, so
        // an audited re-drive falls through and retries.
        if (!keys.Has(instanceId))
        {
            // Re-prune the index too: the key destroy and the index prune below are two writes, so a crash
            // between them leaves a key-less instance whose subject→instance edge still lingers. Re-entry
            // is the only path that ever revisits it, and RemoveInstance is idempotent, so prune here as
            // well — otherwise that edge survives forever and a later erasure reports Complete while the
            // personal-data linkage persists.
            index.RemoveInstance(instanceId);
            heldRegistry?.Clear(instanceId); // it terminated (here or elsewhere) — drop any stale held entry.
            RecordErasureTombstone(instanceId, trigger); // a redelivered erasure still tombstones (idempotent).
            return TerminationOutcome.Terminated;
        }

        int attempts = 0;
        while (true)
        {
            attempts++;
            try
            {
                await contracts.OnRetaining(new RetainingContext(instanceId, idempotencyKey, trigger));
                break;
            }
            catch (Exception error)
            {
                if (attempts >= maxRetainingAttempts)
                {
                    // Quarantine: the retention obligation is unmet, so the key is NOT
                    // destroyed; auto-retry stops and the instance is flagged.
                    string safeError = SafeHeldError(instanceId, error);
                    await contracts.OnRetentionHeld(new RetentionHeldContext(instanceId, attempts, safeError));
                    heldRegistry?.Record(new HeldInstance(
                        instanceId, idempotencyKey, attempts, DateTimeOffset.UtcNow, safeError));
                    metrics?.ShredHeld();
                    return TerminationOutcome.Held;
                }

                if (backoffDelay is not null)
                {
                    await backoffDelay(attempts);
                }
            }
        }

        keys.Destroy(instanceId);
        index.RemoveInstance(instanceId);
        await contracts.OnTerminated(new TerminatedContext(instanceId));
        heldRegistry?.Clear(instanceId); // a re-drive that finally succeeded clears the quarantine.
        RecordErasureTombstone(instanceId, trigger);
        metrics?.ShredCompleted();
        return TerminationOutcome.Terminated;
    }

    // A tombstone is recorded only for an ERASURE (a subject request or the abandoned-instance sweep, both
    // TerminationTrigger.ErasureRequest) so a raise arriving after that shred cannot re-mint and resurrect the
    // flow. A natural completion does NOT tombstone — a completed logical id may legitimately be re-onboarded as
    // a fresh generation. No-op when no tombstone is wired.
    private void RecordErasureTombstone(string instanceId, TerminationTrigger trigger)
    {
        if (trigger == TerminationTrigger.ErasureRequest)
        {
            tombstone?.Record(instanceId);
        }
    }

    /// <summary>The audited human override: re-drive a held instance back into <c>OnRetaining</c>.</summary>
    public Task<TerminationOutcome> ReDriveAsync(string instanceId, IErasureEvent contracts, IdempotencyKey idempotencyKey)
        => TerminateAsync(instanceId, contracts, idempotencyKey, TerminationTrigger.ErasureRequest);

    /// <summary>
    /// Park a <b>failing</b> (not completing) instance instead of terminating it — the step-failure analogue of
    /// the retention-obligation hold. The key is <b>retained</b>, the instance is recorded held with the
    /// scrubbed failure, and <c>OnRetentionHeld</c> fires. No crypto-shred runs, so a transient step failure can
    /// never destroy the sealed journal; recovery is a re-drive (resume) or a deliberate terminate (which then
    /// shreds). Idempotent: if the instance already terminated (completed/erased) its key is gone, so there is
    /// nothing to park and any stale held entry is cleared.
    /// </summary>
    public async Task<TerminationOutcome> QuarantineAsync(
        string instanceId, IErasureEvent contracts, IdempotencyKey idempotencyKey, int attempts, Exception error)
    {
        if (!keys.Has(instanceId))
        {
            heldRegistry?.Clear(instanceId);
            return TerminationOutcome.Terminated;
        }

        string safeError = SafeHeldError(instanceId, error);
        await contracts.OnRetentionHeld(new RetentionHeldContext(instanceId, attempts, safeError));
        heldRegistry?.Record(new HeldInstance(instanceId, idempotencyKey, attempts, DateTimeOffset.UtcNow, safeError));
        metrics?.ShredHeld();
        return TerminationOutcome.Held;
    }

    // The held log is durable and survives the shred, so a raw exception message carrying a subject id would
    // outlive the key that could shred it. Pass it through the same substring guard the framework applies to
    // every other in-clear name (the key is still live here, so the subjects are derivable from the index);
    // a message free of every known subject passes through for diagnosability, one that carries a subject is
    // replaced with a fixed string — the held-log analogue of the adapters' incident-message scrub.
    //
    // Deliberately scans-and-stores `error.Message` (the top-level message), because `error.Message` is the
    // ONLY thing persisted to the held log (HeldInstance.LastError). The scan therefore covers exactly what is
    // stored. This differs from GovernedStepFailure.IsJournalSafe, which scans `error.ToString()` — there the
    // backend journals the whole exception (the full chain), so the full chain must be scanned. Scanning
    // `ToString()` here would over-withhold: a clean outer message would be replaced whenever any nested inner
    // exception merely mentioned a subject, losing diagnosability for no gain (the inner text is never stored).
    private string SafeHeldError(string instanceId, Exception error)
    {
        try
        {
            return RuntimeVisibleName.Require(error.Message, index.SubjectsFor(instanceId), matcher);
        }
        catch (ArgumentException)
        {
            return "retention failed; detail withheld to keep the held log PII-free";
        }
    }
}
