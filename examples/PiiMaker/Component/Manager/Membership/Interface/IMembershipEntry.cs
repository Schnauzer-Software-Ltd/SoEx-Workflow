namespace PiiMaker.Manager.Membership.Interface;

/// <summary>
/// The Membership component's <b>inbound trigger</b> contract — how the rest of the system
/// (a webhook handler reacting to the identity provider, a payment processor callback, any
/// caller with no workflow context) starts a flow or tells a running one that a business
/// event happened. Callers hold nothing but business identity: each operation derives the
/// PII-free instance id from it, so no instance handle, reservation id or flow knowledge
/// crosses this boundary. Start operations seal the seed and start the flow on whatever
/// runtime the host wired; event operations raise a bare event that resumes the waiting
/// flow into its own pre-sealed continuation.
/// <para>This contract is in the <c>*.Manager.Membership.Interface</c> assembly, so the AspNetCore
/// method generator lifts it into a POST controller — one endpoint per trigger. The example UI drives
/// those endpoints: each button is one external event the workflow waits for.</para>
/// </summary>
public interface IMembershipEntry
{
    /// <summary>Starts onboarding (flow A) for an invitee; returns the PII-free instance id.</summary>
    Task<string> StartOnboarding(StartOnboarding command);

    /// <summary>The identity provider confirmed the invitee verified their account.</summary>
    Task AccountVerified(OnboardingIdentity identity);

    /// <summary>The invitee accepted the invite.</summary>
    Task InviteAccepted(OnboardingIdentity identity);

    /// <summary>Starts the renewal cycle (flow B) for a subscriber; returns the PII-free instance id.</summary>
    Task<string> StartRenewal(SubscriberIdentity identity);

    /// <summary>The payment provider confirmed the subscriber updated their payment method.</summary>
    Task PaymentUpdated(SubscriberIdentity identity);

    /// <summary>Starts offboarding (flow C) for a leaver; returns the PII-free instance id.
    /// Native-only fan-out — hosts whose runtime cannot fan out leave this flow unwired.</summary>
    Task<string> StartOffboarding(OffboardingIdentity identity);
}

/// <summary>Everything a caller needs to start onboarding — business identity, no workflow context.</summary>
public sealed record StartOnboarding(string OrgId, string Email, string Offer);

/// <summary>The business identity an onboarding event arrives with (e.g. from an IDP webhook).</summary>
public sealed record OnboardingIdentity(string OrgId, string Email);

/// <summary>The business identity a subscription event arrives with.</summary>
public sealed record SubscriberIdentity(string SubscriberId);

/// <summary>The business identity a leaver is offboarded by.</summary>
public sealed record OffboardingIdentity(string SubjectId);
