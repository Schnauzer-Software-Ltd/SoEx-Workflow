using PiiMaker.Access.Billing.Interface;
using PiiMaker.Access.Identity.Interface;
using PiiMaker.Access.Provisioning.Interface;
using PiiMaker.Engine.Subscription.Interface;
using PiiMaker.iFx.Proxy;
using PiiMaker.Manager.Membership.Interface;
using SoEx.Workflow;

namespace PiiMaker.Manager.Membership.Service;

/// <summary>
/// The native single-step operations of <see cref="MembershipManager"/> — the explicit implementation of
/// <see cref="Interface.Native.IMembershipManager"/>. A native operation returns a PII-free
/// <see cref="StepReceipt"/>: the backend owns the flow and calls the operation per step. Reached only
/// through the native contract a host binds its <c>GovernedStep</c>/endpoint to.
/// </summary>
public sealed partial class MembershipManager : Interface.Native.IMembershipManager
{
    // ---- Flow A: onboarding — native single step -------------------------------------------------

    async Task<StepReceipt> Interface.Native.IMembershipManager.Onboard(OnboardCommand command)
    {
        switch (command)
        {
            case OnboardCommand.LookupUser c:
            {
                bool exists = await Proxy.ForComponent<IIdentityAccess>(this).ExistsAsync(c.Email);
                return new StepReceipt("LookupUser", exists ? "exists" : "new");
            }
            case OnboardCommand.CreateAccount c:
                await Proxy.ForComponent<IIdentityAccess>(this).CreateAccountAsync(c.Email);
                return new StepReceipt("CreateAccount");
            case OnboardCommand.ReserveSubscription c:
            {
                string reservationId =
                    await Proxy.ForComponent<ISubscriptionEngine>(this).ReserveAsync(c.OrgId, c.Offer);
                return new StepReceipt("ReserveSubscription", reservationId);
            }
            case OnboardCommand.SendInvite:
                return new StepReceipt("SendInvite");
            case OnboardCommand.AssignSubscription c:
                await Proxy.ForComponent<ISubscriptionEngine>(this).AssignAsync(c.ReservationId, c.ConfirmedUser);
                return new StepReceipt("AssignSubscription");
            case OnboardCommand.ReleaseReservation c:
                await Proxy.ForComponent<ISubscriptionEngine>(this).ReleaseAsync(c.ReservationId);
                return new StepReceipt("ReleaseReservation");
            case OnboardCommand.Abandon c:
                return new StepReceipt("Abandon", c.Reason);
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }
    }

    // ---- Flow B: subscription renew / dunning — native single step -------------------------------

    async Task<StepReceipt> Interface.Native.IMembershipManager.Renew(RenewCommand command)
    {
        switch (command)
        {
            case RenewCommand.Charge c:
                return await ChargeReceipt(c.SubscriberId, c.Period);
            case RenewCommand.Dun c:
                return await ChargeReceipt(c.SubscriberId, c.Period);
            case RenewCommand.Cancel c:
                await Proxy.ForComponent<ISubscriptionEngine>(this).CancelAsync(c.SubscriberId, c.Reason);
                return new StepReceipt("Cancel");
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }
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

    async Task<StepReceipt> Interface.Native.IMembershipManager.Offboard(OffboardCommand command)
    {
        switch (command)
        {
            case OffboardCommand.Revoke c:
                await Proxy.ForComponent<IProvisioningAccess>(this).RevokeAsync(c.SubjectId, c.System);
                return new StepReceipt("Revoke", c.System);
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }
    }
}
