using SoEx.Abstractions;
using SoEx.Context;

namespace SoEx.Workflow;

/// <summary>
/// Builds the opaque step envelope the durable runtimes journal — the framework-side
/// counterpart of SoEx's client proxy (it constructs the same <see cref="InvocationRequest"/>
/// the <c>ProxyInterceptor</c> builds). Business code never constructs an envelope: it
/// returns a typed step DTO and the framework wraps it here.
/// <para>
/// Every read and write takes the entrypoint contract where the caller knows it. A serializer that
/// binds declared types needs it: the endpoint reads the envelope against the contract, so the
/// envelope must be written against the contract too, or the two disagree about how the step DTO on
/// the wire is named — which is invisible on a concrete DTO and fatal on a closed hierarchy, where the
/// variant cannot be recovered from a payload written the other way. It is optional because the stock
/// open serializer ignores it, and omitting it keeps the older untyped behaviour verbatim.
/// </para>
/// </summary>
public static class WorkflowEnvelope
{
    /// <summary>Wraps a typed step DTO for a consumer operation into the opaque durable envelope.</summary>
    public static byte[] ForStep(
        IMessageSerializer serializer, string operationName, object stepDto, byte[]? ambientContext = null,
        Type? contract = null)
    {
        var request = new InvocationRequest
        {
            ActivityId = null,
            HasResult = true,
            MethodName = operationName,
            Arguments = [stepDto],
            AmbientContext = ambientContext,
        };

        return contract is null
            ? serializer.Serialize(request)
            : serializer.Serialize(request, contract, operationName);
    }

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
    public static string OperationName(IMessageSerializer serializer, byte[] envelope, Type? contract = null) =>
        Request(serializer, envelope, contract).MethodName;

    /// <summary>Reads the typed step DTO an envelope carries (the framework's typed view of a sealed step).</summary>
    public static T StepArg<T>(IMessageSerializer serializer, byte[] envelope, Type? contract = null)
    {
        InvocationRequest request = Request(serializer, envelope, contract);
        if (request.Arguments is not { Length: > 0 } args || args[0] is not T typed)
        {
            throw new ArgumentException($"envelope does not carry a step argument of type {typeof(T).Name}", nameof(envelope));
        }

        return typed;
    }

    /// <summary>The ambient-context bytes an envelope carries — flowed forward onto the next step so the subject persists.</summary>
    public static byte[]? AmbientBytes(IMessageSerializer serializer, byte[] envelope, Type? contract = null) =>
        Request(serializer, envelope, contract).AmbientContext;

    /// <summary>
    /// True if <paramref name="bytes"/> deserialize to a readable plaintext step envelope (an
    /// <see cref="InvocationRequest"/> with an operation name). Sealed bytes are ciphertext and do not — so a
    /// <c>true</c> here is a caller that skipped the seal. Any deserialize failure (the normal case for
    /// ciphertext) is <c>false</c>: not a plaintext envelope. Used by the gateway seal guard as a cheap
    /// shape check; never decrypts.
    /// </summary>
    public static bool LooksLikePlaintextEnvelope(IMessageSerializer serializer, byte[] bytes, Type? contract = null)
    {
        try
        {
            return Request(serializer, bytes, contract) is { MethodName.Length: > 0 };
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The envelope's <see cref="InvocationRequest"/>, read against the contract when one is supplied.
    /// Shared with the metadata extractor so both read a sealed step exactly one way.
    /// </summary>
    internal static InvocationRequest Request(IMessageSerializer serializer, byte[] envelope, Type? contract)
    {
        InvocationRequest? request;
        try
        {
            request = contract is null
                ? serializer.Deserialize<InvocationRequest>(envelope)
                : serializer.Deserialize<InvocationRequest>(envelope, contract);
        }
        catch (Exception failure)
        {
            // Report the failure by exception TYPE and never by message. These bytes are a decrypted step
            // envelope, so the payload is exactly the plaintext the seal exists to keep out of a backend's
            // append-only history — and a serializer is at liberty to quote the offending token back at us
            // (System.Text.Json does). Chaining the original would carry that quoted plaintext into whatever
            // journals the fault, where no crypto-shred reaches it.
            throw new ArgumentException(
                $"payload did not deserialize to an InvocationRequest ({failure.GetType().Name})", nameof(envelope));
        }

        return request ?? throw new ArgumentException("payload did not deserialize to an InvocationRequest", nameof(envelope));
    }
}
