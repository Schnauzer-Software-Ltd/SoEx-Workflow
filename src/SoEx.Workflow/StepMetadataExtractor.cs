using SoEx.Abstractions;
using SoEx.Context;

namespace SoEx.Workflow;

/// <summary>
/// Mechanically extracts the framework-understood stops from a serialized step
/// invocation into <see cref="StepMetadata"/>, without interpreting the business
/// payload. A pure transform over the bytes (no I/O, no clock) — safe to repeat
/// on a replay runtime.
/// </summary>
public sealed class StepMetadataExtractor(IMessageSerializer serializer)
{
    public StepMetadata Extract(byte[] payload, string instanceId, long sequence)
    {
        var request = serializer.Deserialize<InvocationRequest>(payload)
            ?? throw new ArgumentException("payload did not deserialize to an InvocationRequest", nameof(payload));

        IReadOnlyList<string> subjectIds = [];
        bool workflowManaged = false;
        if (request.AmbientContext is { Length: > 0 } ambientBytes)
        {
            var ambient = new AmbientContext(serializer);
            ambient.Deserialize(ambientBytes);
            if (ambient.Contains<SubjectContext>())
            {
                SubjectContext subject = ambient.Get<SubjectContext>();
                subjectIds = subject.SubjectIds ?? [];
                workflowManaged = subject.WorkflowManaged;
            }
        }

        // The step's DTO type identifies the step kind for the idempotency triple;
        // the type — not its contents — is read.
        string dtoType = request.Arguments is { Length: > 0 } && request.Arguments[0] is { } arg
            ? arg.GetType().FullName ?? arg.GetType().Name
            : request.MethodName;

        return new StepMetadata(instanceId, sequence, dtoType, subjectIds, workflowManaged);
    }
}
