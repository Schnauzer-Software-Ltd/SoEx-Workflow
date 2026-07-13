using System.Reflection;
using SoEx.Abstractions;
using SoEx.Context;

namespace SoEx.Workflow;

/// <summary>
/// The per-step context a backend-native flow supplies: the durable instance id and a
/// per-step sequence (both come from the backend's own context — e.g. a Temporal workflow id
/// plus a workflow-owned counter), and the flowed ambient bytes (the subject stop) for the step.
/// </summary>
public readonly record struct StepContext(string InstanceId, long Sequence, byte[]? AmbientContext = null);

/// <summary>
/// The non-generic facet of <see cref="GovernedStep{I}"/> the per-backend drivers depend on. None of
/// these members mention the contract type <c>I</c> (they are byte/object based), so a driver — or a
/// DI-registered backend activity that cannot close over an open generic — depends on this interface
/// and stays non-generic. It is a view onto the one governed-step core, not a parallel implementation.
/// </summary>
public interface IGovernedStep
{
    /// <summary>The consumer's step-operation name, discovered from the contract.</summary>
    string OperationName { get; }

    /// <summary>The serializer the host pipeline uses — reused for envelope/result bytes.</summary>
    IMessageSerializer Serializer { get; }

    /// <summary>
    /// Wraps a typed step DTO into the opaque durable envelope and <b>seals</b> it under the
    /// instance key (minting the key on first use). The returned bytes are ciphertext —
    /// the only form the framework ever hands a backend to persist, so destroying the key at
    /// termination renders the journaled payload unrecoverable (crypto-shred).
    /// </summary>
    byte[] SealStep(string instanceId, object stepDto, byte[]? ambientContext = null);

    /// <summary>Unseals a sealed envelope and returns the ambient bytes it carries (to flow onto the next step).</summary>
    byte[]? AmbientOf(string instanceId, byte[] sealedEnvelope);

    /// <summary>The typed step DTO a sealed envelope carries — lets a native host thread carried state through the framework, never via raw decryption.</summary>
    T UnsealStep<T>(string instanceId, byte[] sealedEnvelope);

    /// <summary>The subject ids the framework knows for a step, read from its (decrypted) ambient bytes.</summary>
    IReadOnlyList<string> SubjectIds(byte[]? ambientContext);

    /// <summary>
    /// Returns <paramref name="name"/> if PII-free, else throws. A runtime-visible name (event/timer/
    /// instance id) is journaled in clear, so it must not carry a subject id (it would survive the shred).
    /// </summary>
    string GuardVisibleName(string name, byte[]? ambientContext);

    /// <summary>
    /// Like <see cref="GuardVisibleName"/> but instance-aware, for scrubbing a to-be-journaled <b>failure</b>
    /// message: when the ambient is unreadable (a decode or guard threw before it was decrypted), it falls back
    /// to the subject index for <paramref name="instanceId"/> — the same still-live source the held-log scrub
    /// uses — instead of treating "no ambient" as "no subject". If neither the ambient nor the index can name
    /// the instance's subjects it throws (withhold), so the caller never journals a message it could not prove
    /// PII-free. Returns <paramref name="name"/> when it is safe.
    /// </summary>
    string GuardVisibleNameForInstance(string name, string instanceId, byte[]? ambientContext);

    /// <summary>
    /// Returns <paramref name="serializedResult"/> if PII-free, else throws. The returned workflow result
    /// is journaled in clear and escapes the termination shred, so it must not carry a subject id.
    /// </summary>
    byte[] GuardResultPiiFree(byte[] serializedResult, byte[]? ambientContext);

    /// <summary>The idempotency key for a sealed step (the drivers use it for the termination write).</summary>
    IdempotencyKey KeyFor(byte[] sealedEnvelope, string instanceId, long sequence);

    /// <summary>Unseal, govern, then dispatch a sealed envelope, returning the entrypoint's result object.</summary>
    Task<object?> DispatchGovernedAsync(byte[] sealedEnvelope, string instanceId, long sequence);
}

/// <summary>
/// Governed step execution. Dispatches one step to the hosted entrypoint through the SoEx
/// pipeline (endpoint pipeline → <c>DefaultDispatcher</c> → <c>entrypoint.&lt;operation&gt;(typedDto)</c>),
/// applying per-step governance (key mint + subject indexing) and — when an idempotency store is
/// supplied — absorbing at-least-once redelivery to a single effect keyed on the
/// <c>(InstanceId, DtoType, Sequence)</c> triple. The entrypoint returns a business result (the native
/// flow) or a <see cref="WorkflowAction"/> (the SoEx-provided portable flow); flow
/// is the backend's concern. Built at the composition root from the already-resolved
/// <see cref="IWorkflowDispatch"/> endpoint + serializer (no hosting dependency of its own).
/// </summary>
public sealed class GovernedStep<I> : IGovernedStep where I : class
{
    private readonly IWorkflowDispatch _endpoint;
    private readonly IIdempotencyStore? _idempotency;
    private readonly StepMetadataExtractor _extractor;
    private readonly IInstanceKeyStore _keys;
    private readonly ISubjectIndex _index;
    private readonly InstanceGovernor _governor;
    private readonly WorkflowSealer _sealer;
    private readonly ISubjectMatcher _matcher;
    private readonly bool _clearJournalResult;
    private readonly WorkflowMetrics? _metrics;

    public GovernedStep(
        IWorkflowDispatch endpoint, IMessageSerializer serializer,
        IIdempotencyStore? idempotency, IInstanceKeyStore keys, ISubjectIndex index,
        string? operationName = null, ISubjectMatcher? subjectMatcher = null, bool clearJournalResult = false,
        WorkflowMetrics? metrics = null, IErasureTombstone? tombstone = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(index);

        _endpoint = endpoint;
        Serializer = serializer;
        _idempotency = idempotency;
        _extractor = new StepMetadataExtractor(serializer);
        _keys = keys;
        _index = index;
        _governor = new InstanceGovernor(keys, index);
        OperationName = ResolveOperation(operationName);
        _sealer = new WorkflowSealer(keys, serializer, OperationName, tombstone);
        _matcher = subjectMatcher ?? SubstringSubjectMatcher.Default;
        _clearJournalResult = clearJournalResult;
        _metrics = metrics;
    }

    // The governed operation: the contract's sole method by default, or — for a multi-operation
    // entrypoint (one component modelling several flows) — the one named by the binding.
    private static string ResolveOperation(string? operationName)
    {
        MethodInfo[] methods = typeof(I).GetMethods();
        if (operationName is null)
        {
            return methods.Length == 1
                ? methods[0].Name
                : throw new InvalidOperationException(
                    $"{typeof(I).Name} has {methods.Length} operations; the binding must name which one to govern");
        }

        return methods.SingleOrDefault(m => m.Name == operationName)?.Name
            ?? throw new InvalidOperationException(
                $"{typeof(I).Name} has no single operation named '{operationName}'");
    }

    /// <summary>The consumer's step-operation name, discovered from the contract.</summary>
    public string OperationName { get; }

    /// <summary>The serializer the host pipeline uses — reused for envelope/result bytes.</summary>
    public IMessageSerializer Serializer { get; }

    /// <summary>
    /// Wraps a typed step DTO into the opaque durable envelope and seals it under the instance
    /// key (minting the key on first use). The bytes are ciphertext — the only form a backend ever
    /// journals — so the termination key destroy crypto-shreds the persisted payload.
    /// </summary>
    public byte[] SealStep(string instanceId, object stepDto, byte[]? ambientContext = null) =>
        _sealer.Seal(instanceId, stepDto, ambientContext);

    /// <summary>Unseals a sealed envelope and returns the ambient bytes it carries (to flow onto the next step).</summary>
    public byte[]? AmbientOf(string instanceId, byte[] sealedEnvelope) =>
        WorkflowEnvelope.AmbientBytes(Serializer, _keys.Decrypt(instanceId, sealedEnvelope));

    /// <summary>The typed step DTO a sealed envelope carries — a native host threads carried state through the framework, not raw decryption.</summary>
    public T UnsealStep<T>(string instanceId, byte[] sealedEnvelope) =>
        WorkflowEnvelope.StepArg<T>(Serializer, _keys.Decrypt(instanceId, sealedEnvelope));

    /// <summary>The subject ids the framework knows for a step, read from its (decrypted) ambient bytes.</summary>
    public IReadOnlyList<string> SubjectIds(byte[]? ambientContext)
    {
        if (ambientContext is not { Length: > 0 } bytes)
        {
            return [];
        }

        var ambient = new AmbientContext(Serializer);
        ambient.Deserialize(bytes);
        return ambient.Contains<SubjectContext>() ? ambient.Get<SubjectContext>().SubjectIds ?? [] : [];
    }

    /// <summary>Guards a runtime-visible name (journaled in clear) against carrying a known subject id.</summary>
    public string GuardVisibleName(string name, byte[]? ambientContext) =>
        RuntimeVisibleName.Require(name, SubjectIds(ambientContext), _matcher);

    /// <summary>Instance-aware name guard: falls back to the subject index when the ambient is unreadable, and
    /// withholds (throws) when neither source can name the instance's subjects. See the interface member.</summary>
    public string GuardVisibleNameForInstance(string name, string instanceId, byte[]? ambientContext)
    {
        if (ambientContext is { Length: > 0 })
        {
            // The step's own ambient is authoritative: guard against exactly the subjects it declares. An empty
            // set here is a genuine "this step carries no subject", so the name is safe to journal.
            return RuntimeVisibleName.Require(name, SubjectIds(ambientContext), _matcher);
        }

        // The ambient could not be read, so it cannot tell us the subjects (this is the defect that let a null
        // ambient pass any exception text through). Fall back to the still-live subject index for the instance —
        // the same source the held-log scrub consults. If the index is empty too, we cannot prove the name is
        // subject-free, so withhold: journal the fixed message rather than risk leaking an unindexed subject.
        IReadOnlyCollection<string> indexed = _index.SubjectsFor(instanceId);
        if (indexed.Count == 0)
        {
            throw new InvalidOperationException(
                $"cannot determine the subjects for instance '{instanceId}' (ambient unreadable, index empty) — withholding to keep the journal PII-free");
        }

        return RuntimeVisibleName.Require(name, indexed, _matcher);
    }

    /// <summary>Guards the returned workflow result (journaled in clear, escapes the shred) against carrying a known subject id.</summary>
    public byte[] GuardResultPiiFree(byte[] serializedResult, byte[]? ambientContext)
    {
        IReadOnlyList<string> subjectIds = SubjectIds(ambientContext);
        RuntimeVisibleName.RequireBytesFree(serializedResult, subjectIds, "the workflow result", _matcher);
        // A subject id serializer-escaped (\uXXXX) or Unicode-decomposed slips the raw byte scan above; the
        // canonical pass decodes those forms and scans again, so the escape evasion is closed here too.
        return JournalCanonicalization.RequireCanonicalFree(serializedResult, subjectIds, "the workflow result", _matcher);
    }

    /// <summary>The idempotency key for a sealed step — the native flow uses it for the termination write.</summary>
    public IdempotencyKey KeyFor(byte[] sealedEnvelope, string instanceId, long sequence) =>
        _extractor.Extract(_keys.Decrypt(instanceId, sealedEnvelope), instanceId, sequence).IdempotencyKey;

    /// <summary>Runs a typed step and returns its typed business result.</summary>
    public async Task<TResult> ExecuteAsync<TResult>(StepContext ctx, object stepDto) =>
        (TResult)(await ExecuteAsync(ctx, stepDto))!;

    /// <summary>Runs a typed step and returns its business result as an object.</summary>
    public Task<object?> ExecuteAsync(StepContext ctx, object stepDto) =>
        DispatchGovernedAsync(SealStep(ctx.InstanceId, stepDto, ctx.AmbientContext), ctx.InstanceId, ctx.Sequence);

    /// <summary>
    /// Unseal under the instance key, govern (key mint + subject indexing) then dispatch, returning the
    /// entrypoint's result object. With an idempotency store the effect applies once per triple and the
    /// recorded result is itself sealed, so the idempotency store never holds plaintext payload.
    /// </summary>
    public async Task<object?> DispatchGovernedAsync(byte[] sealedEnvelope, string instanceId, long sequence)
    {
        try
        {
            object? result = await DispatchGovernedCoreAsync(sealedEnvelope, instanceId, sequence);
            _metrics?.StepExecuted();
            return result;
        }
        catch
        {
            // Per-attempt failure counter: a retried step increments this on each failed attempt, so the metric
            // surfaces retry volume, not just terminal failures.
            _metrics?.StepFailed();
            throw;
        }
    }

    private async Task<object?> DispatchGovernedCoreAsync(byte[] sealedEnvelope, string instanceId, long sequence)
    {
        byte[] stepEnvelope = _keys.Decrypt(instanceId, sealedEnvelope);
        StepMetadata meta = _extractor.Extract(stepEnvelope, instanceId, sequence);
        _governor.OnStep(meta);

        // Native flows author the instance id and the business result themselves; unlike the portable
        // driver, nothing upstream guards them. A subject-bearing instance id is journaled in clear and a
        // subject-bearing result escapes the termination shred, so both are rejected here — on every model,
        // not only the portable flow. The id check is idempotent, so a driver that also guards it is fine.
        RuntimeVisibleName.Require(instanceId, meta.SubjectIds, _matcher);

        if (_idempotency is null)
        {
            return GuardBusinessResult(await DispatchAsync(stepEnvelope), meta.SubjectIds);
        }

        byte[] sealedResult = await _idempotency.ApplyOnceAsync(
            meta.IdempotencyKey,
            async () =>
            {
                object? result = GuardBusinessResult(await DispatchAsync(stepEnvelope), meta.SubjectIds);
                return _keys.Encrypt(instanceId, Serializer.Serialize(result));
            });
        return Serializer.Deserialize<object>(_keys.Decrypt(instanceId, sealedResult));
    }

    // Guards a *native* business result: the consumer's own orchestrator reads it, so the backend journals it
    // in clear (as the activity/step result) and it escapes the termination shred. A portable WorkflowAction is
    // not a business result — the driver re-seals its payloads — so it is exempt; guarding its plaintext would
    // false-positive on data that never journals in clear. The decoded result is guarded, never the sealed
    // recorded bytes.
    //
    // Default-deny: the framework cannot seal a native result without blinding the orchestrator that branches
    // on it, and a known-subject substring net lets any *other* clear PII (a name, address, or DOB that is not
    // the registered subject id) through. So a non-null native result is refused unless the binding has opted
    // in (clearJournalResult) — a conscious "this result is PII-free" declaration — rather than being journaled
    // silently. When opted in, the substring + canonical scans are the backstop.
    private object? GuardBusinessResult(object? result, IReadOnlyList<string> subjectIds)
    {
        if (result is null or WorkflowAction)
        {
            return result;
        }

        if (!_clearJournalResult)
        {
            throw new InvalidOperationException(
                $"the '{OperationName}' step returned a business result ({result.GetType().Name}) that is journaled in clear and " +
                "survives the termination crypto-shred. Return a WorkflowAction (portable flow), return null, or — only after " +
                "ensuring the result is PII-free — opt the binding into clear-journalling (clearJournalResult: true).");
        }

        byte[] serialized = Serializer.Serialize(result);
        RuntimeVisibleName.RequireBytesFree(serialized, subjectIds, "the workflow result", _matcher);
        JournalCanonicalization.RequireCanonicalFree(serialized, subjectIds, "the workflow result", _matcher);
        return result;
    }

    private async Task<object?> DispatchAsync(byte[] stepEnvelope)
    {
        byte[] responseBytes = await _endpoint.DispatchAsync(stepEnvelope);
        InvocationResponse response = Serializer.Deserialize<InvocationResponse>(responseBytes)
            ?? throw new InvalidOperationException("the endpoint did not return an InvocationResponse");
        return response.Response;
    }
}
