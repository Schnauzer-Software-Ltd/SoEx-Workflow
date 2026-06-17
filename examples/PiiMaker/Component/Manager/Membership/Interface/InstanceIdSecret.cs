namespace PiiMaker.Manager.Membership.Interface;

/// <summary>
/// The deployment secret the manager keys its PII-free instance ids under
/// (<see cref="SoEx.Workflow.DeterministicInstanceId.Keyed"/>). The start side and the continue side — here,
/// the one manager — must share it, and it must be stable across restarts so a parked flow's id re-derives
/// on resume. Keep it out of anything journaled in clear; load it from configuration or a secret store in
/// production.
/// </summary>
public sealed record InstanceIdSecret(byte[] Value);
