namespace PiiMaker.Host.Restate;

/// <summary>The native sidecar's <c>/gov-terminate</c> callback payload.</summary>
public sealed record GovTerminateRequest(string InstanceId);
