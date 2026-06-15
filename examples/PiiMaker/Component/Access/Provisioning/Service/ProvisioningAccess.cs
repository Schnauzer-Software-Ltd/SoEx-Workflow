using System.Collections.Concurrent;
using PiiMaker.Access.Provisioning.Interface;

namespace PiiMaker.Access.Provisioning.Service;

/// <summary>Revocation state — a singleton on the access component's host ServiceCollection.</summary>
public sealed class ProvisioningStore
{
    public ConcurrentDictionary<(string, string), byte> Revoked { get; } = new();
}

/// <summary>In-memory <see cref="IProvisioningAccess"/> component — records revocations via the injected store.</summary>
public sealed class ProvisioningAccess(ProvisioningStore store) : IProvisioningAccess
{
    public Task RevokeAsync(string subjectId, string system)
    {
        store.Revoked.TryAdd((subjectId, system), 0);
        return Task.CompletedTask;
    }
}
