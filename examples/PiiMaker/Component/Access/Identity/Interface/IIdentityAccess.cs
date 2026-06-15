namespace PiiMaker.Access.Identity.Interface;

/// <summary>
/// Resource-access for the identity provider (a Zitadel-style directory). A SoEx component contract:
/// every operation is <see cref="System.Threading.Tasks.Task"/>-returning so it dispatches through the
/// pipeline. The subject (email) is PII — never surfaced in a runtime-visible name or the result.
/// </summary>
public interface IIdentityAccess
{
    /// <summary>Whether an account already exists for the email.</summary>
    Task<bool> ExistsAsync(string email);

    /// <summary>Provisions an account for the email (idempotent — a redelivery is a no-op).</summary>
    Task CreateAccountAsync(string email);
}
