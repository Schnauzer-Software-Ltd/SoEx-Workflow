using PiiMaker.Manager.Membership.Interface;
using SoEx.Workflow;

namespace PiiMaker.Manager.Membership.Service;

/// <summary>
/// Native-flow plumbing for the Membership entrypoint: how a backend-authored flow threads the subject as
/// ciphertext. The subject is sealed once into a seed; the flow carries only that opaque seed and names
/// PII-free step kinds. Each governed step recovers the subject through the framework
/// (<see cref="IGovernedStep.UnsealStep{T}"/> / <see cref="IGovernedStep.AmbientOf"/>) inside the activity —
/// off the replay path, in memory only — so the backend journals only ciphertext.
/// <para>Reused by every native host (Elsa/Temporal/DurableTask/Restate); only the flow + host differ.</para>
/// </summary>
public static class MembershipNative
{
    /// <summary>Mints the per-instance key and seals the onboarding subject into the seed the flow threads.</summary>
    public static byte[] SealOnboardSeed(GovernedStep<IMembershipManager> step, string instanceId, string org, string email, string offer)
        => step.SealStep(instanceId, new OnboardCommand.LookupUser(org, email, offer),
                         WorkflowEnvelope.AmbientFor(step.Serializer, SubjectContext.Managed(email)));

    /// <summary>
    /// Runs one native onboarding step from the sealed seed: recover the subject + ambient through the
    /// framework, build the (PII-free-named) kind's command, dispatch the governed <c>OnboardStep</c>
    /// operation, and return the PII-free receipt. The reservation id is the deterministic first reserve.
    /// </summary>
    public static Task<StepReceipt> RunOnboardStep(
        GovernedStep<IMembershipManager> step, string instanceId, long sequence, string kind, byte[] seed)
    {
        OnboardCommand.LookupUser s = step.UnsealStep<OnboardCommand.LookupUser>(instanceId, seed);
        byte[]? ambient = step.AmbientOf(instanceId, seed);
        OnboardCommand command = kind switch
        {
            "lookup" => new OnboardCommand.LookupUser(s.OrgId, s.Email, s.Offer),
            "create" => new OnboardCommand.CreateAccount(s.OrgId, s.Email, s.Offer),
            "reserve" => new OnboardCommand.ReserveSubscription(s.OrgId, s.Email, s.Offer),
            "invite" => new OnboardCommand.SendInvite(s.OrgId, s.Email, s.Offer, "res-1"),
            "assign" => new OnboardCommand.AssignSubscription("res-1", "confirmed-user"),
            _ => throw new ArgumentException($"unknown onboarding step kind '{kind}'", nameof(kind)),
        };
        return step.ExecuteAsync<StepReceipt>(new StepContext(instanceId, sequence, ambient), command);
    }

    /// <summary>Mints the per-instance key and seals the offboarding subject (the leaver) into the seed the fan-out threads.</summary>
    public static byte[] SealOffboardSeed(GovernedStep<IMembershipManager> step, string instanceId, string subjectId)
        => step.SealStep(instanceId, new OffboardCommand.Revoke(subjectId, "*"),
                         WorkflowEnvelope.AmbientFor(step.Serializer, SubjectContext.Managed(subjectId)));

    /// <summary>
    /// Runs one revocation of the offboarding fan-out: recover the subject through the framework, dispatch
    /// the governed <c>OffboardStep</c> for one downstream system. Each call uses a distinct sequence so the
    /// at-least-once effect is idempotent.
    /// </summary>
    public static Task<StepReceipt> RunOffboardStep(
        GovernedStep<IMembershipManager> step, string instanceId, long sequence, string system, byte[] seed)
    {
        OffboardCommand.Revoke s = step.UnsealStep<OffboardCommand.Revoke>(instanceId, seed);
        byte[]? ambient = step.AmbientOf(instanceId, seed);
        return step.ExecuteAsync<StepReceipt>(new StepContext(instanceId, sequence, ambient), new OffboardCommand.Revoke(s.SubjectId, system));
    }
}
