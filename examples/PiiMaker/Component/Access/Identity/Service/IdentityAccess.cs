using System.Collections.Concurrent;
using PiiMaker.Access.Identity.Interface;

namespace PiiMaker.Access.Identity.Service;

/// <summary>The directory's state. Registered as a SINGLETON on the access component's host ServiceCollection,
/// so the (per-call) component shares one store. Concurrency-safe.</summary>
public sealed class IdentityStore
{
    public ConcurrentDictionary<string, byte> Accounts { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// In-memory <see cref="IIdentityAccess"/> component so the examples run with no external directory. State
/// is the injected singleton <see cref="IdentityStore"/>; a production consumer swaps this for the production client.
/// </summary>
public sealed class IdentityAccess(IdentityStore store) : IIdentityAccess
{
    public Task<bool> ExistsAsync(string email) => Task.FromResult(store.Accounts.ContainsKey(email));

    public Task CreateAccountAsync(string email)
    {
        store.Accounts.TryAdd(email, 0);
        return Task.CompletedTask;
    }
}
