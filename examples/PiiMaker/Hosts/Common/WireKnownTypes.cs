using System.Reflection;
using PiiMaker.Manager.Membership.Interface;
using SoEx.Abstractions;
using SoEx.Workflow;

namespace PiiMaker.Hosting;

/// <summary>
/// The types the host's message serializer must be told about. The stock pipeline serializes with
/// System.Text.Json, which binds every value to its declared type, so a value whose declared type is
/// <c>object</c> or an abstract base has to be named up front:
/// <list type="bullet">
/// <item>the framework's own — the subject stop in the ambient context and the portable
/// <see cref="WorkflowAction"/> variants (<see cref="WorkflowKnownTypes.Framework"/>);</item>
/// <item>the step commands — each is a closed hierarchy, and the portable flow carries them in
/// <see cref="WorkflowAction"/>'s <c>object</c> members;</item>
/// <item>the trigger cases, which cross the manager's own binding as a <see cref="TriggerBase"/>.</item>
/// </list>
/// Every host taking part in a flow declares the same list.
/// </summary>
public static class WireKnownTypes
{
    public static KnownTypes All { get; } = new([
        .. WorkflowKnownTypes.Framework,
        .. Variants<OnboardCommand>(),
        .. Variants<RenewCommand>(),
        .. Variants<OffboardCommand>(),
        .. Variants<TriggerBase>(),
    ]);

    // Read off the hierarchy rather than listed by hand, so a new case is covered the day it is declared.
    private static IEnumerable<Type> Variants<T>() =>
        typeof(T).GetNestedTypes(BindingFlags.Public).Where(nested => nested.IsSubclassOf(typeof(T)));
}
