using SoEx.Context;

namespace SoEx.Workflow;

/// <summary>
/// Flows the well-known <see cref="SubjectContext"/> stop across the dispatch edge in
/// both directions, so an entrypoint invoked through the pipeline reads the subject as
/// ambient context (the framework never puts it in a business DTO). Registered in the
/// workflow host; the only stop the framework flows by default.
/// </summary>
public sealed class SubjectContextFlowPolicy : IContextFlowPolicy
{
    public void Incoming(IAmbientContext source, IAmbientContext destination) =>
        source.CopyIfExists<SubjectContext>(destination);

    public void Outgoing(IAmbientContext source, IAmbientContext destination) =>
        source.CopyIfExists<SubjectContext>(destination);

    // Log scope and span tags are durable telemetry that outlives the crypto-shred, so they must not carry the
    // raw subject (it would leave recoverable PII after erasure). Expose only the non-identifying subject COUNT
    // for operational correlation; subject->instance lookup for erasure lives in the tokenised index, not here.
    public IDictionary<string, object> ScopeProperties(IAmbientContext context) =>
        context.Contains<SubjectContext>()
            ? new Dictionary<string, object> { ["SubjectCount"] = context.Get<SubjectContext>().SubjectIds.Count }
            : new Dictionary<string, object>();
}
