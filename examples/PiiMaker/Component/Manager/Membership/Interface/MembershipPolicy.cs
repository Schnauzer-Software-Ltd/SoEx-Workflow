namespace PiiMaker.Manager.Membership.Interface;

/// <summary>Tunable policy for the Membership flows — TTLs, renewal cadence, dunning limits.</summary>
public sealed record MembershipPolicy(
    TimeSpan AccountTtl,
    TimeSpan InviteTtl,
    TimeSpan RenewalInterval,
    TimeSpan DunningBackoff,
    int RenewalPeriods,
    int MaxDunningAttempts)
{
    /// <summary>Demo-friendly defaults.</summary>
    public static MembershipPolicy Default { get; } = new(
        AccountTtl: TimeSpan.FromHours(24),
        InviteTtl: TimeSpan.FromDays(7),
        RenewalInterval: TimeSpan.FromDays(30),
        DunningBackoff: TimeSpan.FromDays(2),
        RenewalPeriods: 3,
        MaxDunningAttempts: 3);
}