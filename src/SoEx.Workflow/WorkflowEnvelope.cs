using SoEx.Abstractions;
using SoEx.Context;

namespace SoEx.Workflow;

/// <summary>
/// Builds the opaque step envelope the durable runtimes journal — the framework-side
/// counterpart of SoEx's client proxy (it constructs the same <see cref="InvocationRequest"/>
/// the <c>ProxyInterceptor</c> builds). Business code never constructs an envelope: it
/// returns a typed step DTO and the framework wraps it here.
/// </summary>
public static class WorkflowEnvelope
{
    /// <summary>Wraps a typed step DTO for a consumer operation into the opaque durable envelope.</summary>
    public static byte[] ForStep(IMessageSerializer serializer, string operationName, object stepDto, byte[]? ambientContext = null) =>
        serializer.Serialize(new InvocationRequest
        {
            ActivityId = null,
            TResult = typeof(WorkflowAction),
            MethodName = operationName,
            Arguments = [stepDto],
            AmbientContext = ambientContext,
        });

    /// <summary>Serializes the subject stop into the ambient-context bytes the envelope carries (null = none).</summary>
    public static byte[]? AmbientFor(IMessageSerializer serializer, SubjectContext? subject)
    {
        if (subject is not { } value)
        {
            return null;
        }

        var bag = new AmbientContext(serializer);
        bag.SetOrReplace(value);
        return bag.Serialize();
    }

    /// <summary>Reads the consumer operation name carried by an envelope (the framework reuses it for later steps).</summary>
    public static string OperationName(IMessageSerializer serializer, byte[] envelope) =>
        Read(serializer, envelope).MethodName;

    /// <summary>Reads the typed step DTO an envelope carries (the framework's typed view of a sealed step).</summary>
    public static T StepArg<T>(IMessageSerializer serializer, byte[] envelope)
    {
        InvocationRequest request = Read(serializer, envelope);
        if (request.Arguments is not { Length: > 0 } args || args[0] is not T typed)
        {
            throw new ArgumentException($"envelope does not carry a step argument of type {typeof(T).Name}", nameof(envelope));
        }

        return typed;
    }

    /// <summary>The ambient-context bytes an envelope carries — flowed forward onto the next step so the subject persists.</summary>
    public static byte[]? AmbientBytes(IMessageSerializer serializer, byte[] envelope) =>
        Read(serializer, envelope).AmbientContext;

    private static InvocationRequest Read(IMessageSerializer serializer, byte[] envelope) =>
        serializer.Deserialize<InvocationRequest>(envelope)
            ?? throw new ArgumentException("payload did not deserialize to an InvocationRequest", nameof(envelope));
}
