using SoEx.Workflow;

namespace SoEx.Method.Workflow;

/// <summary>
/// Builds the per-instance erasure routing a multi-manager <see cref="WorkflowUtility"/> needs: given an
/// instance id, which manager's <see cref="IErasureEvents"/> owns it. The framework's by-type proxy can name
/// only one contract, so when several managers share one utility the composition supplies this routing
/// instead.
/// <para>
/// The routing keys on the instance-id prefix. <see cref="DeterministicInstanceId"/> mints ids as
/// <c>{prefix}-{32 hex}</c>, and the prefix names the flow; namespacing it per manager
/// (e.g. <c>membership.onboard</c>, <c>billing.invoice</c>) makes the owner readable straight off the id with
/// no lookup. An id whose prefix is not in the map resolves to <c>null</c>, which the coordinator surfaces as
/// a not-erased outcome rather than shredding against the wrong contract.
/// </para>
/// </summary>
public static class ErasureRouting
{
    // DeterministicInstanceId.Format emits "{prefix}-{32 hex}"; the hex is a fixed 32 chars with no '-', so the
    // prefix is everything before the trailing "-{32 hex}". Splitting on the last '-' is robust to a prefix
    // that itself contains '-'. Anything not matching that shape is treated as its own prefix (no suffix).
    private const int HexChars = 32;

    /// <summary>The flow/owner prefix an id was minted under — the part before the trailing <c>-{32 hex}</c>.</summary>
    public static string PrefixOf(string instanceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        int suffix = HexChars + 1; // the '-' plus the hex
        return instanceId.Length > suffix && instanceId[^suffix] == '-'
            ? instanceId[..^suffix]
            : instanceId;
    }

    /// <summary>
    /// A resolver that maps an instance id to the owning manager's <see cref="IErasureEvents"/> by its prefix.
    /// Pass the result as <c>resolveErasureFor</c> to <see cref="WorkflowUtility"/>. An unknown prefix yields
    /// <c>null</c> (the owner is unresolved; the coordinator surfaces it, never guesses).
    /// </summary>
    public static Func<string, IErasureEvents?> ByPrefix(IReadOnlyDictionary<string, IErasureEvents> ownerByPrefix)
    {
        ArgumentNullException.ThrowIfNull(ownerByPrefix);
        return instanceId => ownerByPrefix.GetValueOrDefault(PrefixOf(instanceId));
    }
}
