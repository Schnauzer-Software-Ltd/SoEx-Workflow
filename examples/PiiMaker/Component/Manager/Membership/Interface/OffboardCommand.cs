namespace PiiMaker.Manager.Membership.Interface;

/// <summary>
/// Offboarding (flow C) commands. The native flow fans these out — one revocation per downstream system —
/// then archives the employment record at termination via <c>OnRetaining</c>. The leaver is the subject.
/// </summary>
public abstract record OffboardCommand
{
    private OffboardCommand() { }

    public sealed record Revoke(string SubjectId, string System) : OffboardCommand;
}
