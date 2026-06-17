namespace PiiMaker.Manager.Membership.Interface;

/// <summary>
/// The Membership component's <b>inbound trigger</b> contract — how the rest of the system
/// (a webhook handler reacting to the identity provider, a payment processor callback, any
/// caller with no workflow context) starts a flow or tells a running one that a business
/// event happened. One operation, <see cref="Trigger"/>, takes a <see cref="TriggerBase"/> (one case per
/// trigger). Callers hold nothing but business identity: the operation derives the PII-free instance id from
/// it, so no instance handle, reservation id or flow knowledge crosses this boundary, and returns that id.
/// Start triggers seal the seed and start the flow on whatever runtime the host wired; event triggers raise a
/// bare event that resumes the waiting flow into its own pre-sealed continuation.
/// <para>This contract is in the <c>*.Manager.Membership.Interface</c> assembly and is exposed over HTTP by
/// the trigger controller as one POST endpoint, the request body's <c>$type</c> naming the trigger. The
/// example UI drives it: each button fires one trigger.</para>
/// </summary>
public interface IMembershipManager
{
    /// <summary>Fire one inbound trigger; returns the PII-free instance id it derived.</summary>
    Task<string> Trigger(TriggerBase trigger);
}
