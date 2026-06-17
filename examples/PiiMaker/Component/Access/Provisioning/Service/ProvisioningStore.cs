using System.Collections.Concurrent;

namespace PiiMaker.Access.Provisioning.Service;

/// <summary>Revocation state — a singleton on the access component's host ServiceCollection.</summary>
public sealed class ProvisioningStore
{
    public ConcurrentDictionary<(string, string), byte> Revoked { get; } = new();
}
