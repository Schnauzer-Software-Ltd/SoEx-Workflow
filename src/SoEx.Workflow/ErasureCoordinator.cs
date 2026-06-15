namespace SoEx.Workflow;

/// <summary>
/// What the coordinator needs to act on one targeted instance: its erasure events,
/// the idempotency key for the termination write, and its bounded max-remaining duration
/// (<c>null</c> = unbounded). The consumer resolves this per instance id — only it knows
/// an instance's contracts and how long it may still run.
/// </summary>
public sealed record ErasureTarget(
    string InstanceId,
    IErasureEvents Contracts,
    IdempotencyKey IdempotencyKey,
    TimeSpan? MaxRemainingDuration);

/// <summary>The decision taken and the state reached for one instance under an erasure request.</summary>
public sealed record InstanceErasureOutcome(string InstanceId, ErasureAction Action, ErasureState State);

/// <summary>
/// The result of one abandoned-instance sweep: the per-instance outcomes for the aged
/// instances it drove (instances still within their age window are not listed — they were
/// left untouched).
/// </summary>
public sealed record SweepReport(IReadOnlyList<InstanceErasureOutcome> Outcomes)
{
    /// <summary>Aged instances crypto-shredded and pruned this pass.</summary>
    public int Swept => Outcomes.Count(o => o.State == ErasureState.Complete);

    /// <summary>Aged instances that could not shred (retention obligation unmet) and were quarantined.</summary>
    public int Held => Outcomes.Count(o => o.State == ErasureState.Held);
}

/// <summary>
/// The result of processing an erasure request end to end: the legal-clock state, the
/// per-instance decisions/outcomes, and the consumer-facing report at its fidelity
/// floor.
/// </summary>
public sealed record ErasureResult(
    ErasureRequest Request,
    DeadlineStatus Deadline,
    ErasureReport Report,
    IReadOnlyList<InstanceErasureOutcome> Outcomes);

/// <summary>
/// The end-to-end erasure pipeline. Given a request it: stamps the statutory clock,
/// fans out across the subject index to every targeted instance (deduped), decides per
/// instance whether to let it complete naturally or force-terminate it, drives the
/// force-terminations through the <see cref="TerminationCoordinator"/> (crypto-shred +
/// index prune, or quarantine on extraction failure), and reports the whole thing at the
/// available fidelity. It composes the existing seams; it adds no new policy.
/// </summary>
public sealed class ErasureCoordinator(
    ISubjectIndex index,
    StatutoryDeadlineClock clock,
    ErasurePlanner planner,
    TerminationCoordinator termination,
    ErasureReporter reporter,
    IEnumerableInstanceKeyStore? liveInstances = null,
    TimeProvider? time = null,
    IHeldInstanceRegistry? heldRegistry = null,
    IErasureRequestRegistry? requestRegistry = null)
{
    /// <param name="request">The erasure request (its <c>ReceivedAt</c> anchors the legal clock).</param>
    /// <param name="resolve">
    /// Resolves an instance id to its <see cref="ErasureTarget"/>. Returns <c>null</c> for an
    /// instance the consumer cannot resolve — it is reported as still <see cref="ErasureState.Requested"/>
    /// (visible, not silently dropped) and not driven.
    /// </param>
    public Task<ErasureResult> EraseAsync(ErasureRequest request, Func<string, ErasureTarget?> resolve)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(resolve);

        // Fan out across the subject index — one instance may be reached via several subjects.
        List<string> instanceIds = request.Subjects
            .SelectMany(index.InstancesFor)
            .Distinct()
            .ToList();

        return EraseInstancesAsync(request, instanceIds, resolve);
    }

    /// <summary>
    /// Erase a pre-resolved set of instance ids — the path the async front door's drain takes, having admitted
    /// the ids rather than fanning out from subjects. Same per-instance decision, shred/quarantine, and
    /// open-request recording as <see cref="EraseAsync"/>, so a held instance from a drained request is still
    /// recorded for deadline review.
    /// </summary>
    public async Task<ErasureResult> EraseInstancesAsync(
        ErasureRequest request, IReadOnlyList<string> instanceIds, Func<string, ErasureTarget?> resolve)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(instanceIds);
        ArgumentNullException.ThrowIfNull(resolve);

        DeadlineStatus deadline = clock.Evaluate(request);

        List<string> ids = [.. instanceIds.Distinct()];
        var outcomes = new List<InstanceErasureOutcome>(ids.Count);
        var states = new Dictionary<string, ErasureState>(ids.Count);

        foreach (string instanceId in ids)
        {
            if (resolve(instanceId) is not { } target)
            {
                // unresolved — surfaced, never silently dropped
                states[instanceId] = ErasureState.Requested;
                outcomes.Add(new InstanceErasureOutcome(instanceId, ErasureAction.ForceTerminate, ErasureState.Requested));
                continue;
            }

            ErasureAction action = planner.Decide(deadline, target.MaxRemainingDuration);
            ErasureState state;

            if (action == ErasureAction.CompleteNaturally)
            {
                // Leave it: it self-erases at its natural termination within the window.
                // Force-terminating a (possibly multi-subject) instance to satisfy one request
                // would damage co-subjects' still-lawful processing.
                state = ErasureState.InProgress;
            }
            else
            {
                TerminationOutcome outcome = await termination.TerminateAsync(
                    instanceId, target.Contracts, target.IdempotencyKey, TerminationTrigger.ErasureRequest);
                state = outcome == TerminationOutcome.Terminated ? ErasureState.Complete : ErasureState.Held;
            }

            states[instanceId] = state;
            outcomes.Add(new InstanceErasureOutcome(instanceId, action, state));
        }

        // Record the request's not-yet-erased instances so the deadline monitor can re-evaluate them as the
        // statutory window closes (a CompleteNaturally instance trusted to self-erase, or a held one).
        if (requestRegistry is not null)
        {
            List<string> openIds = [.. outcomes.Where(o => o.State != ErasureState.Complete).Select(o => o.InstanceId)];
            if (openIds.Count > 0)
            {
                requestRegistry.Open(new OpenErasureRequest(request.RequestId, deadline.Deadline, openIds, request.Subjects));
            }
        }

        ErasureReport report = reporter.Report(request, deadline, states);
        return new ErasureResult(request, deadline, report, outcomes);
    }

    /// <summary>The result of a held-instance re-drive pass.</summary>
    public sealed record ReDriveReport(IReadOnlyList<InstanceErasureOutcome> Outcomes)
    {
        /// <summary>Held instances that re-drove to a clean termination this pass.</summary>
        public int ReDriven => Outcomes.Count(o => o.State == ErasureState.Complete);

        /// <summary>Held instances that still could not extract and remain quarantined.</summary>
        public int StillHeld => Outcomes.Count(o => o.State == ErasureState.Held);
    }

    /// <summary>One instance whose statutory deadline is at risk while it was left to complete naturally.</summary>
    public sealed record DeadlineEscalation(string RequestId, string InstanceId, DateTimeOffset Deadline, TimeSpan Remaining);

    /// <summary>The result of a deadline-review pass.</summary>
    public sealed record DeadlineReviewReport(IReadOnlyList<InstanceErasureOutcome> Escalated)
    {
        /// <summary>Instances force-terminated because their deadline was at risk.</summary>
        public int Forced => Escalated.Count(o => o.State == ErasureState.Complete);
    }

    /// <summary>
    /// Re-drives every quarantined instance in the held log: an audited retry of its termination extraction.
    /// One left unresolved (no target) keeps its key for a later pass; one that re-drives to a clean termination
    /// is cleared from the log (by the <see cref="TerminationCoordinator"/>). Requires a held log.
    /// </summary>
    public async Task<ReDriveReport> ReDriveHeldAsync(Func<string, ErasureTarget?> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        if (heldRegistry is null)
        {
            throw new InvalidOperationException("ReDriveHeldAsync requires an IHeldInstanceRegistry to be supplied to the coordinator.");
        }

        var outcomes = new List<InstanceErasureOutcome>();
        foreach (HeldInstance held in heldRegistry.Held())
        {
            if (resolve(held.InstanceId) is not { } target)
            {
                outcomes.Add(new InstanceErasureOutcome(held.InstanceId, ErasureAction.ForceTerminate, ErasureState.Held));
                continue;
            }

            // Replay under the key the original attempt used (carried on the held entry), not the maintenance
            // target's generic ("terminal", 0): a consumer's OnRetaining is keyed by it, so re-driving under a
            // different key would write the retention a second time. The target supplies only the contracts/bound.
            TerminationOutcome outcome = await termination.ReDriveAsync(held.InstanceId, target.Contracts, held.IdempotencyKey);
            ErasureState state = outcome == TerminationOutcome.Terminated ? ErasureState.Complete : ErasureState.Held;
            outcomes.Add(new InstanceErasureOutcome(held.InstanceId, ErasureAction.ForceTerminate, state));
        }

        return new ReDriveReport(outcomes);
    }

    /// <summary>
    /// Re-evaluates open erasure requests against the advancing statutory clock. An instance whose key is
    /// already gone (it self-erased) is resolved off its request; a request with none left is closed. An
    /// instance still live within <paramref name="escalateWithin"/> of its deadline — one trusted to complete
    /// naturally that hasn't — is escalated (optional hook) and <b>force-terminated</b> to meet the deadline.
    /// Requires both an erasure-request log and the key store (for the liveness check).
    /// </summary>
    public async Task<DeadlineReviewReport> ReviewDeadlinesAsync(
        TimeSpan escalateWithin, Func<string, ErasureTarget?> resolve, Func<DeadlineEscalation, Task>? onEscalate = null)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        if (requestRegistry is null)
        {
            throw new InvalidOperationException("ReviewDeadlinesAsync requires an IErasureRequestRegistry to be supplied to the coordinator.");
        }

        if (liveInstances is null)
        {
            throw new InvalidOperationException("ReviewDeadlinesAsync requires the key store (IEnumerableInstanceKeyStore) for the liveness check.");
        }

        DateTimeOffset now = (time ?? TimeProvider.System).GetUtcNow();
        var escalated = new List<InstanceErasureOutcome>();

        foreach (OpenErasureRequest open in requestRegistry.Open())
        {
            foreach (string instanceId in open.OpenInstanceIds)
            {
                if (!liveInstances.Has(instanceId))
                {
                    requestRegistry.Resolve(open.RequestId, instanceId); // it self-erased / terminated.
                    continue;
                }

                if (open.Deadline - now > escalateWithin)
                {
                    continue; // still comfortably within the statutory window.
                }

                if (onEscalate is not null)
                {
                    await onEscalate(new DeadlineEscalation(open.RequestId, instanceId, open.Deadline, open.Deadline - now));
                }

                if (resolve(instanceId) is not { } target)
                {
                    escalated.Add(new InstanceErasureOutcome(instanceId, ErasureAction.ForceTerminate, ErasureState.Requested));
                    continue;
                }

                TerminationOutcome outcome = await termination.TerminateAsync(
                    instanceId, target.Contracts, target.IdempotencyKey, TerminationTrigger.ErasureRequest);
                ErasureState state = outcome == TerminationOutcome.Terminated ? ErasureState.Complete : ErasureState.Held;
                if (state == ErasureState.Complete)
                {
                    requestRegistry.Resolve(open.RequestId, instanceId);
                }

                escalated.Add(new InstanceErasureOutcome(instanceId, ErasureAction.ForceTerminate, state));
            }
        }

        return new DeadlineReviewReport(escalated);
    }

    /// <summary>
    /// The abandoned-instance backstop. Where <see cref="EraseAsync"/> is driven by a subject's
    /// request and reaches instances through the subject index, this enumerates the live
    /// (un-terminated) instances the key store still holds and force-terminates every one whose
    /// key was minted longer than <paramref name="olderThan"/> ago — crypto-shredding instances
    /// that were abandoned before their termination hook ran and would otherwise retain their key
    /// (and decryptable payload) indefinitely, even though no erasure request ever names them.
    /// <para>
    /// <paramref name="olderThan"/> is an age threshold, not a liveness probe: it must exceed the
    /// longest legitimate flow duration, or a still-running instance is mistaken for an abandoned
    /// one and shredded mid-flight. The framework owns the shred; the consumer owns the cadence
    /// (schedule this on a timer/cron — see <see cref="ErasureSweepLoop"/>).
    /// </para>
    /// <para>
    /// Ages keys against the <see cref="TimeProvider"/> wall-clock (cutoff = now − <paramref name="olderThan"/>),
    /// so it assumes a <b>monotonically-advancing</b> clock. A sustained backward skew (a large NTP correction,
    /// a VM migration) can defer an abandoned instance's shred past where it would otherwise fire — until the
    /// clock recovers or a later erasure request re-drives the still-indexed instance. The termination is
    /// idempotent, so a deferred-then-resumed sweep never double-shreds; the only effect of skew is a delay.
    /// </para>
    /// </summary>
    /// <param name="olderThan">Minimum age (since key mint) before an un-terminated instance is swept.</param>
    /// <param name="resolve">
    /// Resolves a live instance id to its <see cref="ErasureTarget"/>. An instance the consumer
    /// cannot resolve is reported (<see cref="ErasureState.Requested"/>) and left for a later pass,
    /// never silently dropped — its key survives until it can be resolved and driven.
    /// </param>
    public async Task<SweepReport> SweepAsync(TimeSpan olderThan, Func<string, ErasureTarget?> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        if (liveInstances is null)
        {
            throw new InvalidOperationException(
                "SweepAsync requires an IEnumerableInstanceKeyStore (the live-instance set) to be supplied to the coordinator.");
        }

        DateTimeOffset cutoff = (time ?? TimeProvider.System).GetUtcNow() - olderThan;
        var outcomes = new List<InstanceErasureOutcome>();

        foreach (LiveInstance live in liveInstances.LiveInstances())
        {
            if (live.MintedAt >= cutoff)
            {
                // still within its legitimate lifetime window — a running flow, not an abandoned one
                continue;
            }

            if (resolve(live.InstanceId) is not { } target)
            {
                // unresolved — surfaced, never silently dropped; the key survives for a later pass
                outcomes.Add(new InstanceErasureOutcome(live.InstanceId, ErasureAction.ForceTerminate, ErasureState.Requested));
                continue;
            }

            TerminationOutcome outcome = await termination.TerminateAsync(
                target.InstanceId, target.Contracts, target.IdempotencyKey, TerminationTrigger.ErasureRequest);
            ErasureState state = outcome == TerminationOutcome.Terminated ? ErasureState.Complete : ErasureState.Held;
            outcomes.Add(new InstanceErasureOutcome(live.InstanceId, ErasureAction.ForceTerminate, state));
        }

        return new SweepReport(outcomes);
    }
}
