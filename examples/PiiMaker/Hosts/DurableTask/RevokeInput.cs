namespace PiiMaker.Host.DurableTask;

/// <summary>The activity input for one governed offboarding revocation: the sealed seed, a PII-free
/// sequence, and the target system. Never a plaintext DTO.</summary>
public sealed record RevokeInput(byte[] Seed, string InstanceId, long Seq, string System);
