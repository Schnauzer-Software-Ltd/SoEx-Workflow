namespace PiiMaker.Host.DurableTask;

/// <summary>The orchestration input for the native onboarding flow: the sealed seed + the wait-for-accept
/// timeout (seconds).</summary>
public sealed record NativeInput(byte[] Seed, int TimeoutSeconds);
