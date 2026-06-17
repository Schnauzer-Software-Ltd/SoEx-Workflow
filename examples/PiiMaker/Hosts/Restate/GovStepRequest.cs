namespace PiiMaker.Host.Restate;

/// <summary>The native sidecar's <c>/gov-step</c> callback payload (camelCase on the wire, bound by the
/// ASP.NET minimal-API binder).</summary>
public sealed record GovStepRequest(string InstanceId, long Seq, string Kind, string Data);
