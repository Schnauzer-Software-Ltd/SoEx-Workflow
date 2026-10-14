using System.Text.Json;
using System.Text.Json.Nodes;
using XState.Persistence;

namespace SoEx.Workflow.Statecharts;

/// <summary>
/// Helpers for a chart whose context is JSON, which is every chart imported with <c>MachineConfig.FromJson</c>.
/// </summary>
public static class StatechartJson
{
    private static readonly JsonSerializerOptions Options = SnapshotJson.DefaultOptions();

    /// <summary>
    /// Returns a copy of <paramref name="context"/> with the property <paramref name="name"/> set to
    /// <paramref name="value"/>. A <see cref="JsonElement"/> cannot change, so an assign returns a new one:
    /// <code>
    /// .Action("rememberUser", new AssignAction&lt;JsonElement&gt;(a =&gt;
    ///     a.Context.With("userId", ((DoneActorEvent)a.Event).Output)))
    /// </code>
    /// <para>
    /// The value is written with the snapshot's own JSON options, so it reads back the same way after a park.
    /// </para>
    /// </summary>
    public static JsonElement With(this JsonElement context, string name, object? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        JsonObject copy = context.ValueKind == JsonValueKind.Object
            ? JsonObject.Create(context)!.DeepClone().AsObject()
            : throw new InvalidOperationException(
                $"the machine context is a JSON {context.ValueKind}, not an object, so it has no property '{name}' to set");

        copy[name] = JsonSerializer.SerializeToNode(value, Options);
        return JsonSerializer.SerializeToElement(copy, Options);
    }
}
