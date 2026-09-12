namespace PiiMaker.Host.DurableTask;

/// <summary>The activity input for a governed native onboarding step: the opaque sealed seed + a PII-free
/// sequence. Never a plaintext DTO.</summary>
public sealed record SealedStep(byte[] Seed, string InstanceId, long Seq, byte[]? EventData = null)
{
    /// <summary>The sealed event data for this step, empty when the step was not resumed by a data-carrying
    /// raise. Normalised away from null so a history journaled before this field existed still replays.</summary>
    public byte[] EventData { get; init; } = EventData ?? [];
}
