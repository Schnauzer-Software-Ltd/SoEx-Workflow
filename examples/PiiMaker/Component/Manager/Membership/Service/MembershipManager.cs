using PiiMaker.Access.Retention.Interface;
using PiiMaker.iFx.Proxy;
using PiiMaker.Manager.Membership.Interface;
using SoEx.Workflow;
using WfSubSystem = SoEx.Method.Workflow.SubSystem;

namespace PiiMaker.Manager.Membership.Service;

/// <summary>
/// The one Membership entrypoint, hosted as the entry component of the "membership" subsystem. It reaches its
/// in-subsystem peers (Identity/Subscription/Billing/Provisioning/Retention) as SoEx <b>proxies</b> via
/// <see cref="Proxy.ForComponent{I}(object)"/>, and the Workflow utility — which lives in its own subsystem —
/// via <see cref="Proxy.ForService{I}"/> (a cross-subsystem call). Both are created inline, at the point of
/// use — never via a field or property: SoEx is per-call (a fresh manager per invocation, with its own scope),
/// so a cached proxy would not survive between calls and would mislead. <see cref="MembershipPolicy"/> is a
/// host singleton, constructor-injected.
/// <para>The class is split across three files by the contract each part serves: this file is the inbound
/// trigger seam (<see cref="Interface.IMembershipManager"/>) and the erasure contract
/// (<see cref="IErasureEvents"/>); <c>MembershipManagerPortable</c> implements the portable flow operations
/// (<see cref="Interface.Portable.IMembershipManager"/>, returning a <see cref="WorkflowAction"/>); and
/// <c>MembershipManagerNative</c> implements the native single-step operations
/// (<see cref="Interface.Native.IMembershipManager"/>, returning a PII-free <see cref="StepReceipt"/>). The
/// per-model operations are <b>explicit</b> interface implementations, so each is reached only through its own
/// contract — the contract a host binds its <c>GovernedStep</c>/endpoint to.</para>
/// <para>The durable/persistent workflow plumbing (id derivation, sealing, start/raise-event on the wired runtime,
/// subject recovery, erasure) lives in the <see cref="WfSubSystem.IWorkflowUtility"/> peer (its own subsystem)
/// — the manager stays pure business logic and delegates with business identity + an opaque first step.</para>
/// </summary>
public sealed partial class MembershipManager(MembershipPolicy policy, InstanceIdSecret instanceIdSecret)
    : Interface.IMembershipManager, IErasureEvents
{
    // ---- Inbound triggers (IMembershipManager): start a flow / continue a waiting one ------------
    // The manager derives the PII-free instance id and delegates the durable work — sealing, starting,
    // signalling — to the workflow utility, handing it the flow key, that id, the subject for the ambient,
    // and the opaque first step. A caller (an IDP webhook, a payment callback) needs no instance handle and
    // no shared lookup.
    //
    // The id is HMAC(business identity) under a deployment secret (DeterministicInstanceId.Keyed). Because the
    // id is journaled in clear it must be unguessable: keying it means a party who knows the business identity
    // (org+email, a subscriber id) still cannot derive or confirm the id without the secret. The start side
    // and the continue side both run through this manager, so both hold the same secret (injected as
    // InstanceIdSecret); it is stable across restarts so a parked flow's id re-derives on resume, and it is
    // never part of anything journaled. (The example falls back to a fixed secret so it runs with no setup; a
    // real deployment loads it from configuration or a secret store.)

    public async Task<string> Trigger(TriggerBase trigger)
    {
        WfSubSystem.IWorkflowUtility utility = Proxy.ForService<WfSubSystem.IWorkflowUtility>();
        switch (trigger)
        {
            case TriggerBase.StartOnboarding t:
            {
                string id = DeterministicInstanceId.Keyed(instanceIdSecret.Value, "onboard", t.OrgId, t.Email);
                await utility.StartAsync(
                    "onboard", id, t.Email, new OnboardCommand.LookupUser(t.OrgId, t.Email, t.Offer));
                return id;
            }
            case TriggerBase.AccountVerified t:
            {
                string id = DeterministicInstanceId.Keyed(instanceIdSecret.Value, "onboard", t.OrgId, t.Email);
                await utility.RaiseEventAsync("onboard", id, "account-verified");
                return id;
            }
            case TriggerBase.InviteAccepted t:
            {
                string id = DeterministicInstanceId.Keyed(instanceIdSecret.Value, "onboard", t.OrgId, t.Email);
                await utility.RaiseEventAsync("onboard", id, "invite-accepted");
                return id;
            }
            case TriggerBase.StartRenewal t:
            {
                string id = DeterministicInstanceId.Keyed(instanceIdSecret.Value, "renew", t.SubscriberId);
                await utility.StartAsync(
                    "renew", id, t.SubscriberId, new RenewCommand.Charge(t.SubscriberId, 1));
                return id;
            }
            case TriggerBase.PaymentUpdated t:
            {
                string id = DeterministicInstanceId.Keyed(instanceIdSecret.Value, "renew", t.SubscriberId);
                await utility.RaiseEventAsync("renew", id, "payment-updated");
                return id;
            }
            case TriggerBase.StartOffboarding t:
            {
                string id = DeterministicInstanceId.Keyed(instanceIdSecret.Value, "offboard", t.SubjectId);
                await utility.StartAsync(
                    "offboard", id, t.SubjectId, new OffboardCommand.Revoke(t.SubjectId, "*"));
                return id;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(trigger));
        }
    }

    // ---- Flow D: erasure events (sweep target) ------------------------------------------------

    public async Task OnRetaining(RetainingContext context)
    {
        // Must-retain carve-out, written OUTWARD via the Retention component while the key is still live.
        // Subjects are recovered through the workflow utility (the durable, still-populated subject index).
        IReadOnlyList<string> subjects =
            await Proxy.ForService<WfSubSystem.IWorkflowUtility>().SubjectsForAsync(context.InstanceId);
        if (subjects.Count > 0)
        {
            await Proxy.ForComponent<IRetainedRecordAccess>(this)
                .RetainAsync(context.IdempotencyKey.ToString(), $"membership-record:{string.Join(",", subjects)}");
        }
    }

    public Task OnTerminated(TerminatedContext context)
    {
        return Task.CompletedTask;
    }

    public Task OnRetentionHeld(RetentionHeldContext context)
    {
        return Task.CompletedTask;
    }
}
