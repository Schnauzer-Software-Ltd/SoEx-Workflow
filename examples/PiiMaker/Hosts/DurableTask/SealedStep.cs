namespace PiiMaker.Host.DurableTask;

/// <summary>The activity input for a governed native onboarding step: the opaque sealed seed + a PII-free
/// sequence. Never a plaintext DTO.</summary>
public sealed record SealedStep(byte[] Seed, string InstanceId, long Seq);
