using System.Reflection;
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
    /// <summary>
    /// The number of argument slots an envelope for <paramref name="operationName"/> must carry: the
    /// operation's declared parameter count.
    /// <para>
    /// It has to be exact. SoEx's dispatcher invokes the operation by reflection with the envelope's argument
    /// array verbatim, and reflection does not fill in C# optional parameters — so an array one slot short of
    /// a two-parameter operation throws at dispatch, and one slot long is read past the parameter list while
    /// the argument types are matched. Null entries, on the other hand, pass straight through, which is what
    /// lets a step-only envelope pad the event-data slot. Without a contract the framework cannot ask the
    /// question, and one argument is the shape every envelope had before event data existed.
    /// </para>
    /// </summary>
    public static int Arity(Type? contract, string operationName) =>
        ParametersOf(contract, operationName)?.Length ?? 1;

    private static ParameterInfo[]? ParametersOf(Type? contract, string operationName) =>
        contract?.GetMethods().SingleOrDefault(m => m.Name == operationName)?.GetParameters();

    /// <summary>
    /// The type <paramref name="operationName"/> declares its event data as, or null when it declares none.
    /// An operation writing <c>TData? data = null</c> over a struct declares <c>Nullable&lt;TData&gt;</c>, and
    /// <c>Nullable&lt;T&gt;.IsInstanceOfType</c> is false for a boxed <c>T</c> — so the nullable wrapper is
    /// unwrapped here rather than at each call site, where forgetting it would reject every value-type TData.
    /// </summary>
    public static Type? EventDataType(Type? contract, string operationName)
    {
        if (ParametersOf(contract, operationName) is not { Length: 2 } parameters)
        {
            return null;
        }

        Type slot = parameters[1].ParameterType;
        return Nullable.GetUnderlyingType(slot) ?? slot;
    }

    /// <summary>
    /// Wraps a typed step DTO for a consumer operation into the opaque durable envelope. The arguments are
    /// padded to the operation's <see cref="Arity"/>: the step takes slot 0 and an event-data slot, where the
    /// operation declares one, is left null.
    /// </summary>
    public static byte[] ForStep(
        IMessageSerializer serializer, string operationName, object stepDto, byte[]? ambientContext = null,
        Type? contract = null)
    {
        int arity = Arity(contract, operationName);
        if (arity < 1)
        {
            throw new InvalidOperationException(
                $"'{operationName}' declares no parameters, so there is no slot to dispatch a step DTO into");
        }

        var arguments = new object?[arity];
        arguments[0] = stepDto;

        var request = new InvocationRequest
        {
            ActivityId = null,
            HasResult = true,
            MethodName = operationName,
            Arguments = arguments,
            AmbientContext = ambientContext,
        };

        return contract is null
            ? serializer.Serialize(request)
            : serializer.Serialize(request, contract, operationName);
    }

    /// <summary>
    /// Wraps raise-time event data for a consumer operation that declares a second parameter to receive it:
    /// <c>Arguments = [null, eventData]</c>. Slot 0 is deliberately empty — the step half is not the raiser's
    /// to supply. The flow's own sealed continuation fills it when the two are merged at dispatch, which is
    /// the whole point: the outside world contributes data without having to know what the flow does next.
    /// <para>
    /// The arity and the declared type of slot 1 are both checked <i>here</i>, at seal time, so a raiser who
    /// names the wrong type is refused where the mistake was made rather than inside the instance. It also
    /// makes the two serializer pipelines fail identically: one that binds declared types would reject the
    /// mismatch on the way out, while the stock open one would happily carry it to the far side.
    /// </para>
    /// <para>
    /// The envelope carries no ambient context. On the merge path the flow's own ambient — the one on the
    /// resealed continuation — is authoritative, and a raiser-supplied bag would silently outrank it. A
    /// subject learned from event data is enrolled through <c>WorkflowAction.Subjects</c> instead.
    /// </para>
    /// </summary>
    public static byte[] ForEventData(
        IMessageSerializer serializer, string operationName, object eventData, Type? contract = null)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        Type entrypoint = contract
            ?? throw new InvalidOperationException(
                $"sealing event data needs the entrypoint contract: without it the framework cannot tell whether '{operationName}' accepts event data, nor which type it declares");

        ParameterInfo[] parameters = ParametersOf(entrypoint, operationName)
            ?? throw new InvalidOperationException(
                $"{entrypoint.Name} has no single operation named '{operationName}' to seal event data for");

        if (parameters.Length != 2)
        {
            throw new InvalidOperationException(
                $"'{operationName}' declares {parameters.Length} parameter(s); event data needs an operation whose second parameter receives it");
        }

        Type declared = EventDataType(entrypoint, operationName)!;
        if (!declared.IsInstanceOfType(eventData))
        {
            throw new InvalidOperationException(
                $"'{operationName}' declares its event data as {declared.Name}; a {eventData.GetType().Name} cannot be sealed for it");
        }

        var request = new InvocationRequest
        {
            ActivityId = null,
            HasResult = true,
            MethodName = operationName,
            Arguments = [null, eventData],
            AmbientContext = null,
        };

        return serializer.Serialize(request, entrypoint, operationName);
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

    /// <summary>
    /// The event-data object an envelope carries: slot 1 where it was sealed as event data, otherwise slot 0.
    /// The fallback is what lets a <c>TData</c> that happens to also be the operation's step type arrive
    /// sealed through <see cref="ForStep"/> and still be read as data.
    /// </summary>
    public static object? EventDataArg(IMessageSerializer serializer, byte[] envelope, Type? contract = null)
    {
        object?[] args = Request(serializer, envelope, contract).Arguments;
        if (args.Length == 0)
        {
            return null;
        }

        return args.Length > 1 && args[1] is { } data ? data : args[0];
    }

    /// <summary>
    /// The step envelope <paramref name="stepPlain"/> with <paramref name="eventData"/> in its second
    /// argument slot — the merge performed when a raise carrying data lands on a branch that declared a
    /// continuation. Operation, ambient context and step DTO are the continuation's own; only the data slot
    /// is filled, so everything downstream (idempotency key included) reads an ordinary step.
    /// </summary>
    public static byte[] WithEventData(
        IMessageSerializer serializer, byte[] stepPlain, object eventData, Type? contract = null)
    {
        InvocationRequest request = Request(serializer, stepPlain, contract);
        int arity = Arity(contract, request.MethodName);
        if (arity < 2)
        {
            throw new InvalidOperationException(
                $"'{request.MethodName}' declares {arity} parameter(s); there is no slot to merge event data into");
        }

        var arguments = new object?[arity];
        arguments[0] = request.Arguments.Length > 0 ? request.Arguments[0] : null;
        arguments[1] = eventData;

        var merged = new InvocationRequest
        {
            ActivityId = request.ActivityId,
            HasResult = request.HasResult,
            MethodName = request.MethodName,
            Arguments = arguments,
            AmbientContext = request.AmbientContext,
        };

        return contract is null
            ? serializer.Serialize(merged)
            : serializer.Serialize(merged, contract, request.MethodName);
    }

    /// <summary>
    /// The envelope padded to its operation's <see cref="Arity"/> — the same bytes when it already matches.
    /// <para>
    /// This is what makes an in-flight instance survive a component gaining its event-data parameter. A
    /// continuation sealed before that parameter existed was journaled with one argument, and nothing reseals
    /// it: the flow resumes into exactly those bytes. The dispatcher invokes by reflection with the argument
    /// array verbatim and the deserializer returns exactly the slots the payload held, so without this the
    /// short array reaches a two-parameter operation and throws <c>TargetParameterCountException</c> from
    /// inside the pipeline — on the first step after the deploy, for every parked instance.
    /// </para>
    /// </summary>
    public static byte[] PadToArity(IMessageSerializer serializer, byte[] stepPlain, Type? contract = null)
    {
        InvocationRequest request = Request(serializer, stepPlain, contract);
        int arity = Arity(contract, request.MethodName);
        if (request.Arguments.Length == arity)
        {
            return stepPlain;
        }

        if (request.Arguments.Length > arity)
        {
            // More arguments than the operation declares: the contract lost a parameter under a live instance.
            // Reflection would read past the parameter list, so refuse rather than dispatch something arbitrary.
            throw new InvalidOperationException(
                $"the envelope for '{request.MethodName}' carries {request.Arguments.Length} arguments but the operation declares {arity} — the contract dropped a parameter while instances were in flight");
        }

        var arguments = new object?[arity];
        Array.Copy(request.Arguments, arguments, request.Arguments.Length);

        var padded = new InvocationRequest
        {
            ActivityId = request.ActivityId,
            HasResult = request.HasResult,
            MethodName = request.MethodName,
            Arguments = arguments,
            AmbientContext = request.AmbientContext,
        };

        return contract is null
            ? serializer.Serialize(padded)
            : serializer.Serialize(padded, contract, request.MethodName);
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
