namespace PiiMaker.Access.Provisioning.Interface;

/// <summary>
/// Resource-access for the downstream systems a member has access to (mail, VPN, SaaS apps). A SoEx
/// component contract; Task-returning. Offboarding revokes across all of them (the fan-out the native flow
/// parallelises). Revocation is idempotent.
/// </summary>
public interface IProvisioningAccess
{
    /// <summary>Revokes a subject's access to one system (idempotent on subject+system).</summary>
    Task RevokeAsync(string subjectId, string system);
}
