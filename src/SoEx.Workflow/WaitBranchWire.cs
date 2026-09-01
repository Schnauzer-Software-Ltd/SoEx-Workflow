namespace SoEx.Workflow;

/// <summary>
/// One branch of a <see cref="WorkflowAction.WaitForEvent"/> flattened for a runtime's journal: the
/// event name in clear (guarded PII-free, because every runtime journals it as the delivery key) plus
/// that branch's sealed <c>OnEvent</c> continuation, or empty bytes when the branch declared none.
/// </summary>
public sealed record WaitBranchWire(string EventName, byte[] OnEvent);

/// <summary>
/// The one place a wait is flattened for, and read back from, a runtime journal. Every portable adapter
/// routes through it so the guard-and-seal rules (and the back-compat read) cannot drift between them.
/// </summary>
public static class WaitBranches
{
    /// <summary>
    /// Guards and seals every branch of a wait, in declared order. EVERY branch name is guarded — each one
    /// is journaled in clear as its runtime's delivery key, so a name carrying the subject would survive
    /// the crypto-shred exactly as the first one would.
    /// </summary>
    public static WaitBranchWire[] Flatten(IGovernedStep step, string instanceId, WorkflowAction.WaitForEvent wait, byte[]? ambient) =>
        [.. wait.Branches.Select(b => new WaitBranchWire(
            step.GuardVisibleName(b.EventName, ambient),
            b.OnEvent is { } onEvent ? step.SealStep(instanceId, onEvent, ambient) : []))];

    /// <summary>
    /// The branches a journaled wait carries. An instance parked at a wait when this code is deployed over it
    /// has no branch list in its history, only the legacy single <c>EventName</c>/<c>OnEvent</c> pair, so it is
    /// read back as the one-branch wait it is and keeps replaying. The legacy fields still carry branch 0 for
    /// the same reason in reverse: a single-branch wait journaled here replays unchanged if the deploy is
    /// rolled back. Neither direction is about released versions; both are about a redeploy landing on a
    /// backend that already holds parked instances, which is the normal case on every engine here.
    /// </summary>
    public static WaitBranchWire[] Of(WaitBranchWire[]? branches, string eventName, byte[]? onEvent) =>
        branches is { Length: > 0 } ? branches : [new WaitBranchWire(eventName, onEvent ?? [])];

    /// <summary>Renders a wait's branch names for a diagnostic: <c>'a'</c>, or <c>'a', 'b'</c> for a multi-branch wait.</summary>
    public static string Quoted(IReadOnlyList<WaitBranchWire> branches) =>
        string.Join(", ", branches.Select(b => $"'{b.EventName}'"));
}
