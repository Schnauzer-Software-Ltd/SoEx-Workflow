using System.Reflection;

namespace SoEx.Workflow;

/// <summary>
/// The framework types a host must declare to its message serializer when that serializer binds
/// declared types rather than writing a type marker for every value.
/// <para>
/// The stock System.Text.Json serializer binds declared types, and so does BoundJson; only a host on the
/// Newtonsoft open serializer can ignore this type. The others need it, because two framework values cross a
/// slot typed as <c>object</c>: the subject stop, which the ambient-context bag carries as a dictionary value on every
/// governed step, and the portable flow's <see cref="WorkflowAction"/>, whose variants a serializer can
/// only rebuild once it knows them. Pass these alongside the consumer's own step DTOs — the framework
/// cannot know those — when composing the host.
/// </para>
/// </summary>
public static class WorkflowKnownTypes
{
    /// <summary>
    /// The subject stop plus every <see cref="WorkflowAction"/> variant. Read off the closed hierarchy
    /// rather than listed by hand, so a new variant is covered the day it is declared.
    /// </summary>
    public static IReadOnlyList<Type> Framework { get; } =
    [
        typeof(SubjectContext),
        typeof(EventBranch),
        .. typeof(WorkflowAction).GetNestedTypes(BindingFlags.Public)
            .Where(nested => nested.IsSubclassOf(typeof(WorkflowAction))),
    ];
}
