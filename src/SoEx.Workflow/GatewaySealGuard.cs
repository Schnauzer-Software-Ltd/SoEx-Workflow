using SoEx.Abstractions;

namespace SoEx.Workflow;

/// <summary>
/// Gateway-level guards for the two clear-text artifacts a direct gateway caller controls: the caller-supplied
/// raise id (journaled in clear, so it must be free of a known subject) and the seed/payload bytes (which must
/// be a sealed ciphertext envelope — plaintext handed to a gateway is journaled into append-only backend
/// history where no shred can reach it). A gateway consults this after authorization and before it hands
/// anything to the backend. Wired at the composition root like <see cref="IGatewayAuthorizer"/>; when absent a
/// gateway forwards bytes verbatim (the raw/testing path), so wire it on any network-reachable deployment.
/// </summary>
public sealed class GatewaySealGuard(IMessageSerializer serializer, ISubjectIndex index, ISubjectMatcher? matcher = null)
{
    private readonly ISubjectMatcher _matcher = matcher ?? SubstringSubjectMatcher.Default;

    /// <summary>
    /// Rejects a raise id that carries a subject the framework knows for the instance — a raise id is
    /// caller-supplied and journaled in clear (as the backend's message/event id), so it survives the shred.
    /// A null id (the framework's own no-dedup raise) and an id free of every known subject pass.
    /// </summary>
    public void GuardRaiseId(string instanceId, string? raiseId)
    {
        if (raiseId is null)
        {
            return;
        }

        RuntimeVisibleName.Require(raiseId, index.SubjectsFor(instanceId), _matcher);
    }

    /// <summary>
    /// Rejects <paramref name="bytes"/> that parse as a plaintext workflow envelope. A sealed envelope is
    /// ciphertext and does not deserialize to an <see cref="InvocationRequest"/>, so anything that does is a
    /// caller that bypassed the sealer and would journal plaintext. Cheap: one deserialize attempt, no key or
    /// crypto. Genuinely-sealed bytes fail to deserialize and pass; empty/absent bytes are nothing to journal
    /// and pass. It is a shape check (defence in depth), not authentication — the seal remains the boundary.
    /// </summary>
    public void RequireSealed(byte[]? bytes, string what)
    {
        if (bytes is not { Length: > 0 })
        {
            return;
        }

        if (WorkflowEnvelope.LooksLikePlaintextEnvelope(serializer, bytes))
        {
            throw new InvalidOperationException(
                $"{what} is not a sealed envelope: it deserializes as a plaintext workflow envelope, which would be " +
                "journaled in clear where no crypto-shred can reach it. Seal via WorkflowSealer/WorkflowUtility before " +
                "handing bytes to the gateway.");
        }
    }
}
