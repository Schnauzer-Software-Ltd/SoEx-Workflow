using System.Text.Json;
using XState;
using XState.Persistence;

namespace SoEx.Workflow.Statecharts;

/// <summary>
/// How a machine is bound to a workflow: which events may resume it from outside, and what its side
/// effects mean.
/// </summary>
public sealed class StatechartOptions
{
    /// <summary>
    /// The event names an outside caller may raise at a parked machine, in the order they should be
    /// declared as wait branches.
    /// <para>
    /// These are declared rather than derived. A snapshot does not enumerate what the machine would accept
    /// next, and even if it did, every one of these names is journaled in clear as its runtime's delivery
    /// key — so which names a flow exposes is a decision to make deliberately, not a by-product of the
    /// machine's current state. Declared order is also the tie-break when more than one is deliverable.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> ResumableEvents { get; init; } = [];

    /// <summary>
    /// Runs one of the machine's <see cref="ActionEffect"/>s. Defaults to invoking the effect's own
    /// closure, which is what you want almost always — including for a machine imported from a chart, whose
    /// named actions are bound to real code at import.
    /// <para>
    /// Replace it to wrap that call rather than to replace it: logging, metrics, or a dispatch on
    /// <see cref="ActionEffect.Type"/> for a machine whose actions you would rather resolve yourself. Only an
    /// action the machine knows by NAME has a non-null <c>Type</c> to dispatch on; an inline lambda has none.
    /// </para>
    /// <para>
    /// Effects run inside the governed step, so whatever they touch is covered by the step's retry and
    /// idempotency — make them idempotent, because a redelivered step re-runs them.
    /// </para>
    /// </summary>
    public Action<ActionEffect> RunAction { get; init; } = effect => effect.Exec();

    /// <summary>
    /// Turns the machine context as the snapshot's deserializer produced it back into the machine's own
    /// context type. Required for any machine whose context is not already plain data: a snapshot crosses
    /// the journal as JSON, so an object slot comes back as a dictionary and the machine would reject it.
    /// <para>Use <see cref="JsonContext{TContext}"/> unless the context needs special handling.</para>
    /// </summary>
    public Func<object?, object?>? ContextConverter { get; init; }

    /// <summary>
    /// A <see cref="ContextConverter"/> that re-reads the context as <typeparamref name="TContext"/> through
    /// the same JSON shape the snapshot was written with — the right default whenever the context is a DTO.
    /// </summary>
    public static Func<object?, object?> JsonContext<TContext>()
    {
        JsonSerializerOptions options = SnapshotJson.DefaultOptions();
        return raw => raw is null
            ? null
            : JsonSerializer.Deserialize<TContext>(JsonSerializer.Serialize(raw, options), options);
    }

    /// <summary>
    /// Turns the JSON a raise carried into the payload the machine's event should hold. The default unwraps
    /// a JSON primitive to the matching CLR value and leaves an object or array as a
    /// <see cref="JsonElement"/> for the machine to read.
    /// <para>
    /// The split is deliberate. A machine that expects a name or an amount gets one, without every author
    /// having to unpick a JSON node for the common case; a machine that expects a structured payload gets it
    /// in the one form that is certain to have survived the journal. Replace this to deserialize into a DTO
    /// the machine's transitions can pattern-match on.
    /// </para>
    /// </summary>
    public Func<string, object?> EventDataConverter { get; init; } = Primitive;

    private static object? Primitive(string json)
    {
        JsonElement value = JsonDocument.Parse(json).RootElement.Clone();
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.TryGetInt64(out long whole) ? whole : value.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => value,
        };
    }

    /// <summary>
    /// Reads out of the machine context whatever has to survive a park, as JSON. Null — the default — means
    /// the snapshot carries the whole context by itself, which is true of every context that is plain data.
    /// <para>
    /// Supply it for a context that is NOT data. An SCXML machine's datamodel lives in a JavaScript engine
    /// that cannot be journaled, but the values inside it can be read out — so they travel on the step instead
    /// of inside the snapshot, and <see cref="ApplyContext"/> puts them back. The product cannot do this for
    /// you without taking a dependency on the SCXML importer, and through it a JavaScript engine, on behalf of
    /// every consumer who only ever uses JSON. So the two lines that know the type live with whoever chose it.
    /// </para>
    /// <para>Pairs with <see cref="ScxmlContextIsNotCarried"/>: capture what matters, drop what cannot travel.</para>
    /// </summary>
    public Func<object?, string?>? CaptureContext { get; init; }

    /// <summary>
    /// Puts a captured context back onto the freshly supplied one — the other half of
    /// <see cref="CaptureContext"/>. Receives the context <see cref="ContextConverter"/> produced and the JSON
    /// that was captured when the flow parked.
    /// </summary>
    public Action<object?, string>? ApplyContext { get; init; }

    /// <summary>
    /// Releases whatever the machine context was holding, once the step is over. Called after every step,
    /// including one that threw.
    /// <para>
    /// A step is the whole life of a machine context here. The flow's state travels as a snapshot, so the next
    /// step may well run on another node, and anything this one built has no reader after it returns. For an
    /// SCXML chart that object is a JavaScript engine, and letting a fresh one per step drift out to the
    /// garbage collector is the kind of cost that only shows up under load — so it is disposed deliberately
    /// instead: <c>ReleaseContext = ctx =&gt; ((ScxmlDataModel)ctx!).Engine.Dispose()</c>.
    /// </para>
    /// <para>
    /// Nothing is held between steps either way: a <see cref="StatechartStep"/> keeps only what it was
    /// composed with — the machine, the versions, these options — and never per-instance state. This hook is
    /// about releasing promptly, not about preventing accumulation.
    /// </para>
    /// </summary>
    public Action<object?>? ReleaseContext { get; init; }

    /// <summary>
    /// Persist the snapshot WITHOUT the machine context, letting <see cref="ContextConverter"/> supply one
    /// from the locally-loaded machine on the way back in.
    /// <para>
    /// This exists for SCXML. An SCXML machine's context is a live JavaScript engine, not data, so a snapshot
    /// carrying it cannot be journaled at all — the chart is held locally and loaded at start, and only the
    /// snapshot travels. Dropping the context makes the snapshot travel and a fresh datamodel meet it at the
    /// far side.
    /// </para>
    /// <para>
    /// <b>The cost is exact: SCXML <c>&lt;data&gt;</c> variables do not survive a park.</b> A chart whose logic
    /// is pure state is unaffected; one that keeps values in its datamodel across a wait would lose them.
    /// Anything that must outlive a park belongs in the workflow's own step DTO, where it is sealed and
    /// journaled like any other state. Off by default, because losing context silently is far worse than
    /// refusing to persist it.
    /// </para>
    /// </summary>
    public bool ScxmlContextIsNotCarried { get; init; }

    /// <summary>
    /// The clock used to turn a timer deadline into the delay a durable timer is armed with. Injectable so a
    /// test can advance time; it is read inside the step, off every engine's replay path, and the delay it
    /// produces is journaled once with the step's action.
    /// </summary>
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;
}
