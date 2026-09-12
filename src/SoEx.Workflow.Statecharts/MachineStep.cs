namespace SoEx.Workflow.Statecharts;

/// <summary>
/// The step DTO a machine-backed flow threads forward: the machine's snapshot, which event this step is
/// resuming into, and the deadlines of any timers the machine is waiting on.
/// <para>
/// The snapshot rides as a <b>JSON string</b>, not as the library's <c>PersistedSnapshot</c> record. That
/// record has <c>object?</c> slots (the machine context, a done-machine's output, the events inside its
/// timer ledger), and an untyped slot is exactly what a host serializer has to be told about by name: the
/// allow-listed pipeline binds declared types and the stock one writes a type discriminator, so the same
/// snapshot would need a different registration story on each. Serializing it once with the library's own
/// options collapses that to a string — one shape, both pipelines, nothing to register. It is sealed
/// ciphertext either way, so the extra hop costs only bytes.
/// </para>
/// </summary>
/// <param name="Snapshot">
/// The machine snapshot, serialized with the library's snapshot options. Self-describing: it carries the
/// machine id and version that wrote it, which is what lets the library migrate an old one forward.
/// </param>
/// <param name="EventName">
/// The event this step feeds the machine. Every branch of a wait seals its own continuation, so the branch
/// that fires is the one that says which event it was — the framework needs no side-channel to tell them
/// apart. Empty on the seed step, which starts the machine rather than resuming it.
/// </param>
/// <param name="Deadlines">
/// When each pending machine timer is due, as UTC ticks keyed by timer id. The snapshot records a timer's
/// DECLARED delay and not its deadline, so without this a flow that re-parks for some other reason before a
/// timer fires would restart that timer from full rather than from what is left of it.
/// </param>
public sealed record MachineStep(
    string Snapshot,
    string EventName = "",
    IReadOnlyDictionary<string, long>? Deadlines = null,
    string? ContextData = null)
{
    /// <summary>
    /// Whatever of the machine context had to be captured separately, as JSON — null for a context the
    /// snapshot can carry by itself, which is the ordinary case.
    /// <para>
    /// This exists for a context that is not data: an SCXML machine's datamodel lives in a JavaScript engine,
    /// which no snapshot can hold. The values inside it are readable, though, so they travel here instead and
    /// are put back into a freshly supplied engine on the way in. See
    /// <c>StatechartOptions.CaptureContext</c>.
    /// </para>
    /// </summary>
    public string? ContextData { get; init; } = ContextData;

    /// <summary>Pending timer deadlines (UTC ticks) by timer id; empty rather than null, so the journaled
    /// shape is the same whether or not the machine had timers running.</summary>
    public IReadOnlyDictionary<string, long> Deadlines { get; init; } =
        Deadlines ?? new Dictionary<string, long>(StringComparer.Ordinal);
}

/// <summary>
/// What a raiser sends alongside an event — the second argument of a machine-backed step operation.
/// <para>
/// <paramref name="Data"/> is JSON for the same reason the snapshot is: it becomes the event's payload
/// inside the machine, where its shape is the machine's business and not something the workflow's
/// serializer should have to name.
/// </para>
/// </summary>
public sealed record MachineEventData(string? Data = null);
