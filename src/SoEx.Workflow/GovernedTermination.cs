namespace SoEx.Workflow;

/// <summary>
/// Drives the termination erasure lifecycle for a backend-native flow:
/// <c>OnRetaining</c> → destroy key (crypto-shred) → prune index → <c>OnTerminated</c> (clean),
/// or → <c>OnRetentionHeld</c> (held; key retained). A per-backend governed-workflow base calls
/// this from its termination hook (completion/failure/cancel). Delegates to the unchanged
/// <see cref="TerminationCoordinator"/>.
/// <para>
/// The key store and subject index are <b>required</b> — termination always crypto-shreds and prunes,
/// so an unwired store can never silently skip it. Erasure <i>contracts</i> are optional: an
/// instance with no retention obligation still has its key destroyed (a no-op extract that always
/// succeeds), so the key never outlives the instance.
/// </para>
/// </summary>
public sealed class GovernedTermination
{
    private readonly IErasureEvents _contracts;
    private readonly TerminationCoordinator _termination;

    public GovernedTermination(IErasureEvents? contracts, IInstanceKeyStore keys, ISubjectIndex index, IHeldInstanceRegistry? heldRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(index);

        _contracts = contracts ?? NoErasureEvents.Instance;
        // heldRegistry lets the maintenance backstop find and re-drive a hold that happened on the natural
        // termination path (not just the erasure/sweep path); null = no held-instance tracking (back-compat).
        _termination = new TerminationCoordinator(keys, index, heldRegistry: heldRegistry);
    }

    public Task<TerminationOutcome> TerminateAsync(string instanceId, IdempotencyKey key, TerminationTrigger trigger) =>
        _termination.TerminateAsync(instanceId, _contracts, key, trigger);

    /// <summary>The default for an instance with no retention obligation: extract is a no-op that always succeeds, so termination still shreds the key.</summary>
    private sealed class NoErasureEvents : IErasureEvents
    {
        public static readonly NoErasureEvents Instance = new();
        public Task OnRetaining(RetainingContext context) => Task.CompletedTask;
        public Task OnTerminated(TerminatedContext context) => Task.CompletedTask;
        public Task OnRetentionHeld(RetentionHeldContext context) => Task.CompletedTask;
    }
}
