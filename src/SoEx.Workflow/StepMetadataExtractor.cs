using SoEx.Abstractions;
using SoEx.Context;

namespace SoEx.Workflow;

/// <summary>
/// Mechanically extracts the framework-understood stops from a serialized step
/// invocation into <see cref="StepMetadata"/>, without interpreting the business
/// payload. A pure transform over the bytes (no I/O, no clock) — safe to repeat
/// on a replay runtime.
/// </summary>
public sealed class StepMetadataExtractor(IMessageSerializer serializer, Type? contract = null)
{
    public StepMetadata Extract(byte[] payload, string instanceId, long sequence)
    {
        InvocationRequest request = WorkflowEnvelope.Request(serializer, payload, contract);

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
        //
        // An empty slot 0 is refused rather than fallen back on. It means these bytes are not a step at all:
        // the shape belongs to an event-data seal, which leaves slot 0 for the flow's continuation to fill,
        // and reaching here means it was raised at a branch that declared no continuation. Falling back to the
        // operation name would key the step on the operation, dispatch a null DTO, and read exactly like an
        // ordinary step that chose to do nothing — the quietest possible way to lose a raise.
        if (request.Arguments is not { Length: > 0 } arguments || arguments[0] is not { } arg)
        {
            throw new ArgumentException(
                $"the envelope for '{request.MethodName}' carries no step DTO — event data can only be raised at a branch whose OnEvent continuation receives it",
                nameof(payload));
        }

        string dtoType = arg.GetType().FullName ?? arg.GetType().Name;

        return new StepMetadata(instanceId, sequence, dtoType, subjectIds, workflowManaged);
    }
}
