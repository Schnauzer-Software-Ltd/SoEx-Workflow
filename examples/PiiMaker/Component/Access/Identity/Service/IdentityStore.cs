using System.Collections.Concurrent;

namespace PiiMaker.Access.Identity.Service;

/// <summary>
/// The directory's state. Registered as a SINGLETON on the access component's host ServiceCollection, so the
/// (per-call) component shares one store. Concurrency-safe.
/// </summary>
public sealed class IdentityStore
{
    public ConcurrentDictionary<string, byte> Accounts { get; } = new(StringComparer.OrdinalIgnoreCase);
}
