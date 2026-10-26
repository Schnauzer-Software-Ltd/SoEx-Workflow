namespace SoEx.Workflow;

/// <summary>
/// The lease policy a binding sets: how long an instance may stay idle after each completed step before the
/// abandoned-instance sweep may shred it. <see cref="GovernedStep{I}"/> applies it when an
/// <see cref="IInstanceLeaseRegistry"/> is wired.
/// <list type="bullet">
/// <item>A portable step that returns <see cref="WorkflowAction.WaitForEvent"/> with a timeout, or a
/// <see cref="WorkflowAction.Delay"/>, leases for that period plus <see cref="WaitMargin"/>, and never for less
/// than <see cref="Idle"/>.</item>
/// <item>A portable wait with no timeout, a <see cref="WorkflowAction.RaiseIntoNext"/> or a
/// <see cref="WorkflowAction.Loop"/> leases for <see cref="Idle"/>. A <see cref="WorkflowAction.Complete"/>
/// renews nothing: the termination that follows forgets the lease.</item>
/// <item>A native step leases for <see cref="StepContext.Lease"/> when the flow sets it, else for
/// <see cref="Idle"/>. Set it on the step before a long wait, because the framework cannot see a native wait.</item>
/// </list>
/// </summary>
public sealed record InstanceLeaseOptions
{
    /// <summary>The lease a step gets when it declares nothing longer. Also the floor under a derived wait lease.</summary>
    public TimeSpan Idle { get; init; } = TimeSpan.FromDays(1);

    /// <summary>Added to a portable wait's timeout or delay, so a timer that fires late or a step that retries
    /// after it is not mistaken for an abandoned instance.</summary>
    public TimeSpan WaitMargin { get; init; } = TimeSpan.FromHours(1);

    /// <summary>One day idle, one hour margin.</summary>
    public static InstanceLeaseOptions Default { get; } = new();

    /// <summary>
    /// The idle period a completed step leases its instance for, or <c>null</c> when the step renews nothing (it
    /// completed the flow). <paramref name="declared"/> is a native step's own <see cref="StepContext.Lease"/>
    /// and is used exactly as given.
    /// </summary>
    public TimeSpan? LeaseAfter(object? result, TimeSpan? declared = null) =>
        result switch
        {
            WorkflowAction.Complete => null,
            _ when declared is { } given => given,
            WorkflowAction.WaitForEvent { Timeout: { } timeout } => AtLeastIdle(timeout),
            WorkflowAction.Delay delay => AtLeastIdle(delay.Duration),
            _ => Idle,
        };

    private TimeSpan AtLeastIdle(TimeSpan wait)
    {
        // Saturate rather than overflow: a wait declared with a huge timeout leases "forever", not negative.
        TimeSpan withMargin = wait > TimeSpan.MaxValue - WaitMargin ? TimeSpan.MaxValue : wait + WaitMargin;
        return withMargin > Idle ? withMargin : Idle;
    }
}
