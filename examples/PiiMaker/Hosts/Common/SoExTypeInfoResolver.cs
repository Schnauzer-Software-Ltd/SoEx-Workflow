using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using PiiMaker.Generated;

namespace PiiMaker.Hosting;

/// <summary>
/// Teaches System.Text.Json the polymorphic shapes the lifted interface(s) carry, driven by the
/// generator-emitted <see cref="SoExKnownTypes"/> registry ($type discriminator = full type name). For the
/// trigger seam the DTOs are flat, so this is effectively a no-op today; it is wired regardless so that any
/// abstract-record command hierarchy lifted in future round-trips over the wire without hand-maintenance.
/// </summary>
public sealed class SoExTypeInfoResolver : DefaultJsonTypeInfoResolver
{
    public override JsonTypeInfo GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        JsonTypeInfo jsonTypeInfo = base.GetTypeInfo(type, options);

        if (SoExKnownTypes.TryGetType(jsonTypeInfo.Type, out Type[] derivedTypes))
        {
            jsonTypeInfo.PolymorphismOptions = new JsonPolymorphismOptions
            {
                TypeDiscriminatorPropertyName = "$type",
                IgnoreUnrecognizedTypeDiscriminators = true,
                UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
            };
            foreach (Type derivedType in derivedTypes)
            {
                jsonTypeInfo.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(derivedType, derivedType.FullName!));
            }
        }

        return jsonTypeInfo;
    }
}
