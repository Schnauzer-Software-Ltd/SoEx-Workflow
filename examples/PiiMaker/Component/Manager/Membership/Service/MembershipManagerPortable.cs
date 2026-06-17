using PiiMaker.Access.Billing.Interface;
using PiiMaker.Access.Identity.Interface;
using PiiMaker.Engine.Subscription.Interface;
using PiiMaker.iFx.Proxy;
using PiiMaker.Manager.Membership.Interface;
using SoEx.Workflow;

namespace PiiMaker.Manager.Membership.Service;

/// <summary>
/// The portable-flow operations of <see cref="MembershipManager"/> — the explicit implementation of
/// <see cref="Interface.Portable.IMembershipManager"/>. A portable operation returns a
/// <see cref="WorkflowAction"/>: the component <i>is</i> the flow, and the generic driver drives it on any
/// runtime. Reached only through the portable contract a host binds its <c>GovernedStep</c>/endpoint to.
/// </summary>
public sealed partial class MembershipManager : Interface.Portable.IMembershipManager
{
    // ---- Flow A: onboarding (portable) -----------------------------------------------------------

    async Task<WorkflowAction> Interface.Portable.IMembershipManager.Onboard(OnboardCommand command)
    {
        switch (command)
        {
            case OnboardCommand.LookupUser c:
            {
                bool exists = await Proxy.ForComponent<IIdentityAccess>(this).ExistsAsync(c.Email);
                return exists
                    ? new OnboardCommand.ReserveSubscription(c.OrgId, c.Email, c.Offer).Raise()
                    : new OnboardCommand.CreateAccount(c.OrgId, c.Email, c.Offer).Raise();
            }
            case OnboardCommand.CreateAccount c:
                return await CreateAccountThenAwaitVerification(c);
            case OnboardCommand.ReserveSubscription c:
                return await ReserveThenInvite(c);
            case OnboardCommand.SendInvite c:
                return new WorkflowAction.WaitForEvent(
                    "invite-accepted", policy.InviteTtl,
                    OnTimeout: new OnboardCommand.ReleaseReservation(c.ReservationId),
                    OnEvent: new OnboardCommand.AssignSubscription(c.ReservationId, c.Email));
            case OnboardCommand.AssignSubscription c:
                return await AssignAndComplete(c);
            case OnboardCommand.ReleaseReservation c:
                return await ReleaseAndComplete(c);
            case OnboardCommand.Abandon c:
                return new WorkflowAction.Complete($"abandoned:{c.Reason}");
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }
    }

    // Each wait pre-seals its own OnEvent continuation, so a bare "this happened" raise (no
    // payload, no flow knowledge) resumes the flow — the seam IMembershipManager relies on.
    private async Task<WorkflowAction> CreateAccountThenAwaitVerification(OnboardCommand.CreateAccount c)
    {
        await Proxy.ForComponent<IIdentityAccess>(this).CreateAccountAsync(c.Email);
        return new WorkflowAction.WaitForEvent(
            "account-verified", policy.AccountTtl,
            OnTimeout: new OnboardCommand.Abandon("account not verified"),
            OnEvent: new OnboardCommand.ReserveSubscription(c.OrgId, c.Email, c.Offer));
    }

    private async Task<WorkflowAction> ReserveThenInvite(OnboardCommand.ReserveSubscription c)
    {
        string reservationId = await Proxy.ForComponent<ISubscriptionEngine>(this).ReserveAsync(c.OrgId, c.Offer);
        return new OnboardCommand.SendInvite(c.OrgId, c.Email, c.Offer, reservationId).Raise();
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

    // ---- Flow B: subscription renew / dunning (portable) -----------------------------------------

    async Task<WorkflowAction> Interface.Portable.IMembershipManager.Renew(RenewCommand command)
    {
        switch (command)
        {
            case RenewCommand.Charge c:
                return await Settle(c.SubscriberId, c.Period, dunningAttempt: 0);
            case RenewCommand.Dun c:
                return await Settle(c.SubscriberId, c.Period, c.Attempt);
            case RenewCommand.Cancel c:
                return await CancelAndComplete(c);
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }
    }

    // Charge the period; on success continue-as-new into the next period (or complete the run); on a
    // decline, back off and wait for "payment-updated", escalating to cancel once dunning is exhausted.
    private async Task<WorkflowAction> Settle(string subscriberId, int period, int dunningAttempt)
    {
        if (await Proxy.ForComponent<IBillingAccess>(this).ChargeAsync(subscriberId, period))
        {
            await Proxy.ForComponent<IBillingAccess>(this).InvoiceAsync(subscriberId, period);
            if (period < policy.RenewalPeriods)
            {
                // continue-as-new into the next period
                return new WorkflowAction.Loop(new RenewCommand.Charge(subscriberId, period + 1));
            }

            return new WorkflowAction.Complete($"renewed:{period}");
        }

        if (dunningAttempt >= policy.MaxDunningAttempts)
        {
            return new RenewCommand.Cancel(subscriberId, "dunning exhausted").Raise();
        }

        // "payment-updated" retries the charge immediately (OnEvent); the backoff timer retries anyway.
        return new WorkflowAction.WaitForEvent(
            "payment-updated", policy.DunningBackoff,
            OnTimeout: new RenewCommand.Dun(subscriberId, period, dunningAttempt + 1),
            OnEvent: new RenewCommand.Dun(subscriberId, period, dunningAttempt + 1));
    }

    private async Task<WorkflowAction> CancelAndComplete(RenewCommand.Cancel c)
    {
        await Proxy.ForComponent<ISubscriptionEngine>(this).CancelAsync(c.SubscriberId, c.Reason);
        return new WorkflowAction.Complete($"cancelled:{c.Reason}");
    }
}
