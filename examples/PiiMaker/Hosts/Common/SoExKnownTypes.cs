using PiiMaker.Manager.Membership.Interface;

namespace PiiMaker.Generated;

/// <summary>
/// Maps a base type to the derived types System.Text.Json must know to round-trip a polymorphic argument over
/// the wire. <see cref="PiiMaker.Hosting.SoExTypeInfoResolver"/> consults this and registers each derived type
/// under its full type name as the <c>$type</c> discriminator; it is a no-op for types with no entry here.
/// <para>The trigger seam takes a polymorphic <see cref="TriggerBase"/>, so its cases are registered. This was
/// previously emitted by the <c>SoEx.Method.Generators.AspNetCore</c> source generator; it is hand-written
/// because that generator collides on the manager's same-named <c>Native</c>/<c>Portable</c> contracts.</para>
/// </summary>
public static class SoExKnownTypes
{
    public static bool TryGetType(Type baseType, out Type[] derivedTypes)
    {
        derivedTypes = baseType.FullName switch
        {
            "PiiMaker.Manager.Membership.Interface.TriggerBase" =>
            [
                typeof(TriggerBase.StartOnboarding),
                typeof(TriggerBase.AccountVerified),
                typeof(TriggerBase.InviteAccepted),
                typeof(TriggerBase.StartRenewal),
                typeof(TriggerBase.PaymentUpdated),
                typeof(TriggerBase.StartOffboarding),
            ],
            _ => [],
        };
        return derivedTypes.Length > 0;
    }
}
