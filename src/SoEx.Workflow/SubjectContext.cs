namespace SoEx.Workflow;

/// <summary>
/// The well-known ambient stop carrying the PII subject set for an instance and
/// whether subject handling is workflow-managed or externally-managed. Flowed by
/// an <c>IContextFlowPolicy</c>; never carried inside business DTOs. A value type,
/// as SoEx ambient stops are.
/// </summary>
public readonly record struct SubjectContext(IReadOnlyList<string> SubjectIds, bool WorkflowManaged)
{
    public static SubjectContext Managed(params string[] subjectIds) => new(subjectIds, true);

    public static SubjectContext External(params string[] subjectIds) => new(subjectIds, false);
}
