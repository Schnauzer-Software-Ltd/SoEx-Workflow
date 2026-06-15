using SoEx.Method.Workflow.External;
using SoEx.Workflow;

namespace SoEx.Method.Workflow;

/// <summary>
/// The Workflow utility implementation — a reusable IDesign Method component that owns the consumer-side
/// durable/persistent workflow plumbing. It serves two distinct faces of itself: the
/// <see cref="SubSystem.IWorkflowUtility"/> a peer entry component (e.g. a manager) proxies to
/// (start/raise-event/recover) and the <see cref="External.IWorkflowUtility"/> the host calls as a system client
/// (erase/sweep). It holds the durable stores (key store, subject index — supplied on its ServiceCollection,
/// read back by the host to wire the governed step/termination) and the host-wired <see cref="WorkflowSeam"/>.
/// It reaches the entry component's erasure termination through the framework <see cref="IErasureEvents"/>
/// proxy via <see cref="SoEx.Proxy.ForService{I}"/> — a cross-subsystem call (the entry component is in another
/// subsystem, possibly another host); it resolves from the same channel the host's termination uses, so the
/// utility adds no second registration on that type.
/// <para>
/// When more than one entry component (manager) shares this one utility and runtime, each owns a distinct
/// erasure termination, and the single by-type framework proxy can no longer name which one to drive. The
/// optional <paramref name="resolveErasureFor"/> supplies that routing: given an instance id, it returns the
/// owning manager's <see cref="IErasureEvents"/> (or <c>null</c> when the owner can't be resolved, which the
/// coordinator surfaces rather than silently shredding against the wrong contract). When it is not supplied
/// the utility keeps the single-manager behaviour, resolving the one framework proxy.
/// </para>
/// </summary>
public sealed class WorkflowUtility(
    WorkflowSeam seam,
    IInstanceKeyStore keys,
    ISubjectIndex index,
    IHeldInstanceRegistry? heldRegistry = null,
    IErasureRequestRegistry? requestRegistry = null,
    Func<string, IErasureEvents?>? resolveErasureFor = null,
    IPendingErasureRequests? pending = null)
    : SubSystem.IWorkflowUtility, External.IWorkflowUtility
{
    // ---- SubSystem face: what the manager proxies to -------------------------------------------------

    public async Task StartAsync(string flowKey, string instanceId, string subject, object firstStep)
    {
        // The instance id is journaled in clear by the backend the moment Start is called and survives the
        // shred, so reject one that carries the start subject here — at the consumer-facing seam, before the
        // gateway journals it. The gateway itself can't do this (it only sees the sealed seed); the blessed id
        // is a DeterministicInstanceId, PII-free by construction, so a clean caller never trips this.
        RuntimeVisibleName.Require(instanceId, [subject]);

        WorkflowSeam.FlowSeam flow = seam.For(flowKey);
        byte[] seed = flow.Sealer.Seal(instanceId, firstStep,
            WorkflowEnvelope.AmbientFor(flow.Serializer, SubjectContext.Managed(subject)));
        await flow.Gateway.StartAsync(instanceId, seed);
    }

    public Task RaiseEventAsync(string flowKey, string instanceId, string eventName)
    {
        // The event name is journaled in clear by the backend (signal name / promise key / bookmark) and
        // survives the shred, so reject one carrying a known subject before it reaches the gateway — the
        // raise-side analogue of the step seam's name guard. Subjects come from the still-live subject index.
        RuntimeVisibleName.Require(eventName, index.SubjectsFor(instanceId));
        return seam.For(flowKey).Gateway.RaiseEventAsync(instanceId, eventName);
    }

    public Task<string[]> SubjectsForAsync(string instanceId) =>
        Task.FromResult<string[]>([.. index.SubjectsFor(instanceId)]);

    // ---- External face: what the host calls ----------------------------------------------------------

    public Task<string> RequestEraseAsync(string subject)
    {
        // Async front door: resolve the subject to its instances NOW (a cheap index lookup), admit those
        // PII-free instance ids durably, and return at once. The shred is NOT done here — a drain pass runs it
        // later. Resolving at admit keeps the durable record PII-free (no recoverable subject at rest); a
        // crash between this call and the drain still leaves the admitted ids to process (accept-before-ack).
        // The request id is a PII-free, stable handle, so a duplicate "forget S" collapses to one request.
        if (pending is null)
        {
            throw new InvalidOperationException(
                "RequestEraseAsync requires an IPendingErasureRequests store to be supplied to the utility.");
        }

        string requestId = DeterministicInstanceId.For("erase", subject);
        IReadOnlyList<string> instanceIds = [.. index.InstancesFor(subject)];
        pending.Admit(new PendingErasureRequest(requestId, DateTimeOffset.UtcNow, instanceIds));
        return Task.FromResult(requestId);
    }

    public async Task<int> DrainEraseRequestsAsync()
    {
        // One pass: drive each admitted request's instances to crypto-shred through the coordinator (the same
        // per-instance decision, per-manager shred, and held/open-request recording the request path uses), so
        // a held instance from a drained request is still recorded for deadline review. An unresolved owner is
        // left with its key; re-delivery is safe (a re-drain of an already-shredded instance is a no-op).
        if (pending is null)
        {
            throw new InvalidOperationException(
                "DrainEraseRequestsAsync requires an IPendingErasureRequests store to be supplied to the utility.");
        }

        ErasureCoordinator coordinator = Coordinator();
        int drained = 0;
        foreach (PendingErasureRequest request in pending.Pending())
        {
            await coordinator.EraseInstancesAsync(
                new ErasureRequest(request.RequestId, request.ReceivedAt, []), request.InstanceIds, Target);
            pending.Drained(request.RequestId);
            drained++;
        }

        return drained;
    }

    public async Task<int> SweepAbandonedAsync(int olderThanSeconds)
    {
        // The request-independent backstop (one pass): age the live key set and force-terminate instances
        // abandoned before their termination hook ran. The sweep logic is owned here; the host owns the cadence.
        SweepReport report = await Coordinator().SweepAsync(TimeSpan.FromSeconds(olderThanSeconds), Target);
        return report.Outcomes.Count;
    }

    public async Task<int> ReDriveHeldAsync()
    {
        // One pass: audited retry of every quarantined instance's termination extraction.
        ErasureCoordinator.ReDriveReport report = await Coordinator().ReDriveHeldAsync(Target);
        return report.ReDriven;
    }

    public async Task<int> ReviewDeadlinesAsync(int escalateWithinSeconds)
    {
        // One pass: force-terminate instances whose statutory deadline is at risk (trusted to self-erase but
        // still live within the window). Needs the request log; with none wired, there is nothing to review.
        if (requestRegistry is null)
        {
            return 0;
        }

        ErasureCoordinator.DeadlineReviewReport report =
            await Coordinator().ReviewDeadlinesAsync(TimeSpan.FromSeconds(escalateWithinSeconds), Target);
        return report.Forced;
    }

    // The resolve callback every maintenance pass shares: the owning entry component's erasure termination,
    // keyed by the termination idempotency key. Unbounded (the utility does not know an instance's bound).
    // With one manager this is the single framework proxy; with several, resolveErasureFor names the owner's
    // contract per instance. A null contract (owner unresolved) returns a null target, which the coordinator
    // surfaces as a not-erased outcome — never a silent drop and never a shred against the wrong contract.
    private ErasureTarget? Target(string instanceId)
    {
        IErasureEvents? contracts = resolveErasureFor is not null
            ? resolveErasureFor(instanceId)
            : SoEx.Proxy.ForService<IErasureEvents>();
        return contracts is null
            ? null
            : new ErasureTarget(instanceId, contracts, new IdempotencyKey(instanceId, "terminal", 0), MaxRemainingDuration: null);
    }

    // A fresh coordinator per call — the planner/clock/reporter are stateless; the durable state is the shared
    // index, key store, and (optional) maintenance logs. SweepAsync needs the enumerable key set.
    private ErasureCoordinator Coordinator() => new(
        index, new StatutoryDeadlineClock(), new ErasurePlanner(),
        new TerminationCoordinator(keys, index, heldRegistry: heldRegistry), new ErasureReporter(),
        keys as IEnumerableInstanceKeyStore, time: null, heldRegistry: heldRegistry, requestRegistry: requestRegistry);
}
