namespace SoEx.Workflow;

/// <summary>Host-registration guards for workflow-hosted Managers.</summary>
public static class WorkflowRegistration
{
    /// <summary>
    /// Fail-fast at registration: a subsystem entrypoint hosted on a workflow binding must
    /// implement all three erasure events. (The interface enforces this at
    /// compile time when an entrypoint declares it; this catches an entrypoint that omits
    /// the declaration entirely.)
    /// </summary>
    public static void RequireErasureEvent(Type managerType)
    {
        ArgumentNullException.ThrowIfNull(managerType);

        if (!typeof(IErasureEvent).IsAssignableFrom(managerType))
        {
            throw new InvalidOperationException(
                $"'{managerType.Name}' is hosted on a workflow binding and must implement " +
                "IErasureEvent (OnRetaining / OnTerminated / OnRetentionHeld); " +
                "a deliberate no-op must be written explicitly.");
        }
    }
}
