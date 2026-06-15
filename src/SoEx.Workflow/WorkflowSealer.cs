using SoEx.Abstractions;

namespace SoEx.Workflow;

/// <summary>
/// Seals typed step DTOs into the opaque durable envelope under the per-instance key —
/// the seal-side of <see cref="GovernedStep{I}"/>, standalone. A business component that
/// starts or continues flows (e.g. a webhook handler turning "account verified" into a
/// workflow start) needs to seal a seed without holding the dispatch endpoint — which it
/// could not: the endpoint dispatches <i>into</i> that component. The operation name is
/// the consumer operation the sealed step targets (e.g. <c>nameof(IThing.Onboard)</c>).
/// </summary>
public sealed class WorkflowSealer(IInstanceKeyStore keys, IMessageSerializer serializer, string operationName)
{
    /// <summary>
    /// Wraps a typed step DTO into the opaque durable envelope and seals it under the
    /// instance key (minting the key on first use). The bytes are ciphertext — the only
    /// form a backend ever journals — so the termination key destroy crypto-shreds them.
    /// </summary>
    public byte[] Seal(string instanceId, object stepDto, byte[]? ambientContext = null)
    {
        keys.Mint(instanceId);
        return keys.Encrypt(instanceId, WorkflowEnvelope.ForStep(serializer, operationName, stepDto, ambientContext));
    }
}
