namespace PiiMaker.Manager.Membership.Interface;

/// <summary>
/// Subscription renewal/dunning (flow B) commands. The renewal cycle continues-as-new across periods; a
/// declined charge enters dunning. The subscriber id is the subject.
/// </summary>
public abstract record RenewCommand
{
    private RenewCommand() { }

    public sealed record Charge(string SubscriberId, int Period) : RenewCommand;
    public sealed record Dun(string SubscriberId, int Period, int Attempt) : RenewCommand;
    public sealed record Cancel(string SubscriberId, string Reason) : RenewCommand;
}
