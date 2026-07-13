namespace SoEx.Method.Workflow.SubSystem;

/// <summary>
/// The result of a start submitted through <see cref="IWorkflowUtility.StartAsync"/>: whether a run already
/// owned the (deterministic) instance id. Carried as <b>data</b> because a caller reaches the utility across a
/// proxy boundary that decouples exceptions — an already-exists condition a runtime adapter detects deep down
/// cannot be conveyed as an exception, so it is returned as a value.
/// <para>A record (not an enum) on purpose: the proxy channel serializes a bare enum as a number and cannot
/// unbox it back to the enum type on the return path, whereas a record round-trips cleanly.</para>
/// </summary>
/// <param name="AlreadyExists"><c>true</c> when a run already owns the id and nothing new was started; widen
/// the identity (a fresh attempt/epoch segment) to begin a distinct run. <c>false</c> when a new run started.</param>
public sealed record StartOutcome(bool AlreadyExists);
