namespace PiiMaker.Manager.Membership.Interface;

/// <summary>
/// The PII-free result a native step returns. It names the step (a kind, never a subject value) and an
/// optional non-PII detail (e.g. a reservation id). It is journaled in clear and survives the shred, so it
/// must carry no subject — the framework enforces this.
/// </summary>
public sealed record StepReceipt(string Step, string? Detail = null);
