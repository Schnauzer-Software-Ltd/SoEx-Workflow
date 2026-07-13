namespace PiiMaker.Manager.Membership.Interface;

/// <summary>
/// The outcome of a <see cref="IMembershipManager.Trigger"/> call: the PII-free instance id the trigger
/// targeted, and whether a <b>start</b> was deduplicated because a run already owns that id. A caller (the HTTP
/// seam) turns <see cref="AlreadyStarted"/> into a meaningful "already onboarded — bump the attempt to restart"
/// response instead of the raw backend error a duplicate start would otherwise surface. Event triggers (which
/// signal a running flow rather than start one) always report <see cref="AlreadyStarted"/> = <c>false</c>.
/// </summary>
public sealed record TriggerResult(string InstanceId, bool AlreadyStarted);
