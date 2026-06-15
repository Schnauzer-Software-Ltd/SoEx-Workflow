using PiiMaker.Access.Billing.Interface;
using PiiMaker.Access.Identity.Interface;
using PiiMaker.Access.Provisioning.Interface;
using PiiMaker.Access.Retention.Interface;
using PiiMaker.Engine.Subscription.Interface;
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
/// so a cached proxy would not survive between calls and would mislead.
/// <see cref="MembershipPolicy"/> is a host singleton, constructor-injected. Models every demo flow as a
/// distinct operation: portable ops return a <see cref="WorkflowAction"/>; native ops return a PII-free
/// <see cref="StepReceipt"/>. Also implements <see cref="IMembershipEntry"/> — the inbound trigger seam — and
/// <see cref="IErasureEvents"/> so erasure sweeps these instances, writing the must-retain carve-out
/// outward via the Retention component.
/// <para>The durable/persistent workflow plumbing (id derivation, sealing, start/raise-event on the wired runtime,
/// subject recovery, erasure) lives in the <see cref="WfSubSystem.IWorkflowUtility"/> peer (its own subsystem)
/// — the manager stays pure business logic and delegates with business identity + an opaque first step.</para>
/// </summary>
public sealed class MembershipManager(MembershipPolicy policy) : IMembershipManager, IMembershipEntry, IErasureEvents
{
    // ---- Inbound triggers (IMembershipEntry): start a flow / continue a waiting one --------------
    // The manager derives the PII-free instance id (a stateless hash of business identity) and delegates the
    // durable work — sealing, starting, signalling — to the workflow utility, handing it the flow key, that id,
    // the subject for the ambient, and the opaque first step. A caller (an IDP webhook, a payment callback)
    // needs no instance handle and no shared lookup.
    //
    // SECURITY NOTE — this example uses the UNKEYED DeterministicInstanceId.For for store-free simplicity. That
    // id is non-secret and CONFIRMABLE: anyone who guesses the business identity (org+email, a subscriber id)
    // re-derives the same id and can probe its status forever, since the id is journaled in clear. A real
    // deployment that wants the id unguessable should use DeterministicInstanceId.Keyed(secret, ...) with a
    // stable deployment secret held by both the start side and the continue side (kept out of anything journaled
    // in clear). We keep .For here only so the demo needs no secret distribution; do not copy it as-is.

    public async Task<string> StartOnboarding(StartOnboarding command)
    {
        string instanceId = DeterministicInstanceId.For("onboard", command.OrgId, command.Email); // demo: unkeyed/confirmable — see SECURITY NOTE above
        await Proxy.ForService<WfSubSystem.IWorkflowUtility>().StartAsync("onboard", instanceId, command.Email,
            new OnboardCommand.LookupUser(command.OrgId, command.Email, command.Offer));
        return instanceId;
    }

    public Task AccountVerified(OnboardingIdentity identity) =>
        Proxy.ForService<WfSubSystem.IWorkflowUtility>()
            .RaiseEventAsync("onboard", DeterministicInstanceId.For("onboard", identity.OrgId, identity.Email), "account-verified");

    public Task InviteAccepted(OnboardingIdentity identity) =>
        Proxy.ForService<WfSubSystem.IWorkflowUtility>()
            .RaiseEventAsync("onboard", DeterministicInstanceId.For("onboard", identity.OrgId, identity.Email), "invite-accepted");

    public async Task<string> StartRenewal(SubscriberIdentity identity)
    {
        string instanceId = DeterministicInstanceId.For("renew", identity.SubscriberId);
        await Proxy.ForService<WfSubSystem.IWorkflowUtility>()
            .StartAsync("renew", instanceId, identity.SubscriberId, new RenewCommand.Charge(identity.SubscriberId, 1));
        return instanceId;
    }

    public Task PaymentUpdated(SubscriberIdentity identity) =>
        Proxy.ForService<WfSubSystem.IWorkflowUtility>()
            .RaiseEventAsync("renew", DeterministicInstanceId.For("renew", identity.SubscriberId), "payment-updated");

    public async Task<string> StartOffboarding(OffboardingIdentity identity)
    {
        string instanceId = DeterministicInstanceId.For("offboard", identity.SubjectId);
        await Proxy.ForService<WfSubSystem.IWorkflowUtility>()
            .StartAsync("offboard", instanceId, identity.SubjectId, new OffboardCommand.Revoke(identity.SubjectId, "*"));
        return instanceId;
    }

    // ---- Flow A: onboarding ----------------------------------------------------------------------

    public async Task<WorkflowAction> Onboard(OnboardCommand command) => command switch
    {
        OnboardCommand.LookupUser c => await Proxy.ForComponent<IIdentityAccess>(this).ExistsAsync(c.Email)
            ? Raise(new OnboardCommand.ReserveSubscription(c.OrgId, c.Email, c.Offer))
            : Raise(new OnboardCommand.CreateAccount(c.OrgId, c.Email, c.Offer)),
        OnboardCommand.CreateAccount c => await CreateAccountThenAwaitVerification(c),
        OnboardCommand.ReserveSubscription c => await ReserveThenInvite(c),
        OnboardCommand.SendInvite c => new WorkflowAction.WaitForEvent(
            "invite-accepted", policy.InviteTtl,
            OnTimeout: new OnboardCommand.ReleaseReservation(c.ReservationId),
            OnEvent: new OnboardCommand.AssignSubscription(c.ReservationId, c.Email)),
        OnboardCommand.AssignSubscription c => await AssignAndComplete(c),
        OnboardCommand.ReleaseReservation c => await ReleaseAndComplete(c),
        OnboardCommand.Abandon c => new WorkflowAction.Complete($"abandoned:{c.Reason}"),
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };

    public async Task<StepReceipt> OnboardStep(OnboardCommand command) => command switch
    {
        OnboardCommand.LookupUser c => new StepReceipt("LookupUser", await Proxy.ForComponent<IIdentityAccess>(this).ExistsAsync(c.Email) ? "exists" : "new"),
        OnboardCommand.CreateAccount c => await AfterAsync(() => Proxy.ForComponent<IIdentityAccess>(this).CreateAccountAsync(c.Email), "CreateAccount"),
        OnboardCommand.ReserveSubscription c => new StepReceipt("ReserveSubscription", await Proxy.ForComponent<ISubscriptionEngine>(this).ReserveAsync(c.OrgId, c.Offer)),
        OnboardCommand.SendInvite => new StepReceipt("SendInvite"),
        OnboardCommand.AssignSubscription c => await AfterAsync(() => Proxy.ForComponent<ISubscriptionEngine>(this).AssignAsync(c.ReservationId, c.ConfirmedUser), "AssignSubscription"),
        OnboardCommand.ReleaseReservation c => await AfterAsync(() => Proxy.ForComponent<ISubscriptionEngine>(this).ReleaseAsync(c.ReservationId), "ReleaseReservation"),
        OnboardCommand.Abandon c => new StepReceipt("Abandon", c.Reason),
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };

    // Each wait pre-seals its own OnEvent continuation, so a bare "this happened" raise (no
    // payload, no flow knowledge) resumes the flow — the seam IMembershipEntry relies on.
    private async Task<WorkflowAction> CreateAccountThenAwaitVerification(OnboardCommand.CreateAccount c)
    {
        await Proxy.ForComponent<IIdentityAccess>(this).CreateAccountAsync(c.Email);
        return new WorkflowAction.WaitForEvent("account-verified", policy.AccountTtl,
            OnTimeout: new OnboardCommand.Abandon("account not verified"),
            OnEvent: new OnboardCommand.ReserveSubscription(c.OrgId, c.Email, c.Offer));
    }

    private async Task<WorkflowAction> ReserveThenInvite(OnboardCommand.ReserveSubscription c)
    {
        string reservationId = await Proxy.ForComponent<ISubscriptionEngine>(this).ReserveAsync(c.OrgId, c.Offer);
        return Raise(new OnboardCommand.SendInvite(c.OrgId, c.Email, c.Offer, reservationId));
    }

    private async Task<WorkflowAction> AssignAndComplete(OnboardCommand.AssignSubscription c)
    {
        await Proxy.ForComponent<ISubscriptionEngine>(this).AssignAsync(c.ReservationId, c.ConfirmedUser);
        return new WorkflowAction.Complete($"assigned:{c.ReservationId}");   // PII-free
    }

    private async Task<WorkflowAction> ReleaseAndComplete(OnboardCommand.ReleaseReservation c)
    {
        await Proxy.ForComponent<ISubscriptionEngine>(this).ReleaseAsync(c.ReservationId);
        return new WorkflowAction.Complete($"released:{c.ReservationId}");   // compensation, PII-free
    }

    // ---- Flow B: subscription renew / dunning ----------------------------------------------------

    public async Task<WorkflowAction> Renew(RenewCommand command) => command switch
    {
        RenewCommand.Charge c => await Settle(c.SubscriberId, c.Period, dunningAttempt: 0),
        RenewCommand.Dun c => await Settle(c.SubscriberId, c.Period, c.Attempt),
        RenewCommand.Cancel c => await CancelAndComplete(c),
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };

    public async Task<StepReceipt> RenewStep(RenewCommand command) => command switch
    {
        RenewCommand.Charge c => await ChargeReceipt(c.SubscriberId, c.Period),
        RenewCommand.Dun c => await ChargeReceipt(c.SubscriberId, c.Period),
        RenewCommand.Cancel c => await AfterAsync(() => Proxy.ForComponent<ISubscriptionEngine>(this).CancelAsync(c.SubscriberId, c.Reason), "Cancel"),
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };

    // Charge the period; on success continue-as-new into the next period (or complete the run); on a
    // decline, back off and wait for "payment-updated", escalating to cancel once dunning is exhausted.
    private async Task<WorkflowAction> Settle(string subscriberId, int period, int dunningAttempt)
    {
        if (await Proxy.ForComponent<IBillingAccess>(this).ChargeAsync(subscriberId, period))
        {
            await Proxy.ForComponent<IBillingAccess>(this).InvoiceAsync(subscriberId, period);
            return period < policy.RenewalPeriods
                ? new WorkflowAction.Loop(new RenewCommand.Charge(subscriberId, period + 1))   // continue-as-new
                : new WorkflowAction.Complete($"renewed:{period}");
        }

        // "payment-updated" retries the charge immediately (OnEvent); the backoff timer retries anyway.
        return dunningAttempt < policy.MaxDunningAttempts
            ? new WorkflowAction.WaitForEvent("payment-updated", policy.DunningBackoff,
                OnTimeout: new RenewCommand.Dun(subscriberId, period, dunningAttempt + 1),
                OnEvent: new RenewCommand.Dun(subscriberId, period, dunningAttempt + 1))
            : Raise(new RenewCommand.Cancel(subscriberId, "dunning exhausted"));
    }

    private async Task<WorkflowAction> CancelAndComplete(RenewCommand.Cancel c)
    {
        await Proxy.ForComponent<ISubscriptionEngine>(this).CancelAsync(c.SubscriberId, c.Reason);
        return new WorkflowAction.Complete($"cancelled:{c.Reason}");
    }

    private async Task<StepReceipt> ChargeReceipt(string subscriberId, int period)
    {
        bool ok = await Proxy.ForComponent<IBillingAccess>(this).ChargeAsync(subscriberId, period);
        if (ok)
        {
            await Proxy.ForComponent<IBillingAccess>(this).InvoiceAsync(subscriberId, period);
        }

        return new StepReceipt("Charge", ok ? "ok" : "declined");
    }

    // ---- Flow C: offboarding (native-only fan-out) -----------------------------------------------

    public async Task<StepReceipt> OffboardStep(OffboardCommand command) => command switch
    {
        OffboardCommand.Revoke c => await AfterAsync(() => Proxy.ForComponent<IProvisioningAccess>(this).RevokeAsync(c.SubjectId, c.System), "Revoke", c.System),
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };

    // ---- Flow D: erasure events (sweep target) ------------------------------------------------

    public async Task OnRetaining(RetainingContext context)
    {
        // Must-retain carve-out, written OUTWARD via the Retention component while the key is still live.
        // Subjects are recovered through the workflow utility (the durable, still-populated subject index).
        IReadOnlyList<string> subjects = await Proxy.ForService<WfSubSystem.IWorkflowUtility>().SubjectsForAsync(context.InstanceId);
        if (subjects.Count > 0)
        {
            await Proxy.ForComponent<IRetainedRecordAccess>(this)
                .RetainAsync(context.IdempotencyKey.ToString(), $"membership-record:{string.Join(",", subjects)}");
        }
    }

    public Task OnTerminated(TerminatedContext context) => Task.CompletedTask;

    public Task OnRetentionHeld(RetentionHeldContext context) => Task.CompletedTask;

    // ---- helpers ---------------------------------------------------------------------------------

    private static WorkflowAction Raise(object next) => new WorkflowAction.RaiseIntoNext(next);

    private static async Task<StepReceipt> AfterAsync(Func<Task> effect, string step, string? detail = null)
    {
        await effect();
        return new StepReceipt(step, detail);
    }
}
