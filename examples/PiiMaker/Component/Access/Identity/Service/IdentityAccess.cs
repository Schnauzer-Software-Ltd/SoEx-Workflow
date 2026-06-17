using PiiMaker.Access.Identity.Interface;

namespace PiiMaker.Access.Identity.Service;

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
