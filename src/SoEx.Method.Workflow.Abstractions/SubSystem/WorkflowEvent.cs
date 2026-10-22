namespace SoEx.Method.Workflow.SubSystem;

/// <summary>
/// A business event to raise onto a running flow through <see cref="IWorkflowUtility.RaiseEventAsync"/>. The
/// constructor takes what every raise needs; the optional parts are init properties, so a call names what it
/// sets rather than passing them by position.
/// <para>A DTO rather than a parameter list because the seam crosses a proxy, and SoEx refuses a same-named
/// overload at composition: a new optional part is a new property here, not a new operation signature.</para>
/// </summary>
/// <param name="FlowKey">The flow the instance runs, as wired on the utility's seam.</param>
/// <param name="InstanceId">The PII-free instance id, normally re-derived with <c>DeterministicInstanceId</c>.</param>
/// <param name="Name">The event name. It is journaled in clear, so it must carry no subject.</param>
public sealed record WorkflowEvent(string FlowKey, string InstanceId, string Name)
{
    /// <summary>
    /// What the raiser knows that the flow could not. The waiting flow resumes into its own pre-sealed
    /// continuation either way — the data does not replace it, it travels with it and reaches the step operation
    /// as a second argument. It is sealed under the instance key, so it is only ever journaled as ciphertext and
    /// the termination shred reaches it. The flow's step operation must declare a parameter to receive it, or
    /// the raise is refused at the seal. Leave it null when the event itself is the whole message.
    /// </summary>
    public object? Data { get; init; }

    /// <summary>
    /// Makes the raise idempotent: a raise that repeats an id the instance already handled returns quietly and
    /// does not resume the flow again. Use it when the same event can arrive more than once (a retried webhook,
    /// a replayed sweep); every route that raises the same occurrence must send the same id. It removes only
    /// duplicates of a raise that already succeeded — a raise that arrives while the instance is not waiting is
    /// still refused. The id is journaled in clear, so it must carry no subject. Some runtimes need an
    /// idempotency store wired to honour it and refuse an id without one.
    /// </summary>
    public string? RaiseId { get; init; }
}
