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
    /// True when the governed operation declares a second parameter, and so can receive raise-time event
    /// data alongside its step. An adapter reads it to decide whether carrying event data is worth the
    /// journal space; the dispatch path enforces it either way.
    /// </summary>
    bool AcceptsEventData { get; }

    /// <summary>
    /// Wraps a typed step DTO into the opaque durable envelope and <b>seals</b> it under the
    /// instance key (minting the key on first use). The returned bytes are ciphertext —
    /// the only form the framework ever hands a backend to persist, so destroying the key at
    /// termination renders the journaled payload unrecoverable (crypto-shred).
    /// </summary>
    byte[] SealStep(string instanceId, object stepDto, byte[]? ambientContext = null);

    /// <summary>
    /// Wraps raise-time event <b>data</b> into the opaque durable envelope and seals it under the instance
    /// key — the form a raiser uses to feed the flow's own declared continuation rather than to supply the
    /// next step. Refused unless the governed operation declares a parameter to receive it.
    /// </summary>
    byte[] SealEventData(string instanceId, object eventData);

    /// <summary>Unseals a sealed envelope and returns the ambient bytes it carries (to flow onto the next step).</summary>
    byte[]? AmbientOf(string instanceId, byte[] sealedEnvelope);

    /// <summary>The typed step DTO a sealed envelope carries — lets a native host thread carried state through the framework, never via raw decryption.</summary>
    T UnsealStep<T>(string instanceId, byte[] sealedEnvelope);

    /// <summary>The subject ids the framework knows for a step, read from its (decrypted) ambient bytes.</summary>
    IReadOnlyList<string> SubjectIds(byte[]? ambientContext);

    /// <summary>
    /// Folds the subjects an action declared (<see cref="WorkflowAction.Subjects"/>) into the step's subject
    /// context and returns the ambient bytes the rest of the step must use — the extended set for an action
    /// that enrolled someone, the bytes unchanged for one that did not.
    /// <para>
    /// Two things happen, and both are needed. The new subjects are indexed immediately, because a flow can
    /// park at a wait for days and until the edge exists an erasure request for that person does not reach
    /// this instance. They are also carried on the returned ambient, which every continuation is sealed with,
    /// so the name guards cover them from here on.
    /// </para>
    /// <para>
    /// Call it after the step returns and <b>before</b> guarding or flattening: a step that enrolls a subject
    /// and in the same action names an event after them must be caught, which only works if the guard sees the
    /// extended set. An externally-managed flow keeps deferring indexing, exactly as it does for the subject it
    /// started with. A <see cref="WorkflowAction.Delay"/> that declares subjects is rejected — it seals no next
    /// step for them to travel on.
    /// </para>
    /// </summary>
    byte[]? EnrollSubjects(string instanceId, byte[]? ambientContext, WorkflowAction action);

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

    /// <summary>
    /// As <see cref="DispatchGovernedAsync(byte[],string,long)"/>, additionally merging a sealed event-data
    /// envelope into the step before anything reads it — the dispatch a driver performs when a raise carrying
    /// data resumed a branch that had declared a continuation. Empty or null event data is the plain dispatch.
    /// <para>
    /// The merge is invisible downstream on purpose: the idempotency key is still taken from the
    /// continuation's own DTO type, so a raise carrying data and a bare raise of the same branch collapse to
    /// the same effect — which is what keeps a redelivered raise safe.
    /// </para>
    /// </summary>
    Task<object?> DispatchGovernedAsync(byte[] sealedEnvelope, byte[]? sealedEventData, string instanceId, long sequence);
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
    private readonly Type? _eventDataType;
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
        _extractor = new StepMetadataExtractor(serializer, typeof(I));
        _keys = keys;
        _index = index;
        _governor = new InstanceGovernor(keys, index);
        MethodInfo operation = ResolveOperation(operationName);
        OperationName = operation.Name;
        _eventDataType = WorkflowEnvelope.EventDataType(typeof(I), operation.Name);
        AcceptsEventData = _eventDataType is not null;
        _sealer = new WorkflowSealer(keys, serializer, OperationName, tombstone, typeof(I));
        _matcher = subjectMatcher ?? SubstringSubjectMatcher.Default;
        _clearJournalResult = clearJournalResult;
        _metrics = metrics;
    }

    // The governed operation: the contract's sole method by default, or — for a multi-operation
    // entrypoint (one component modelling several flows) — the one named by the binding.
    private static MethodInfo ResolveOperation(string? operationName)
    {
        MethodInfo[] methods = typeof(I).GetMethods();
        MethodInfo resolved;
        if (operationName is null)
        {
            resolved = methods.Length == 1
                ? methods[0]
                : throw new InvalidOperationException(
                    $"{typeof(I).Name} has {methods.Length} operations; the binding must name which one to govern");
        }
        else
        {
            resolved = methods.SingleOrDefault(m => m.Name == operationName)
                ?? throw new InvalidOperationException(
                    $"{typeof(I).Name} has no single operation named '{operationName}'");
        }

        // One parameter is a step; two is a step followed by raise-time event data. Anything else cannot be
        // dispatched at all — the framework builds the argument array itself and has nothing to fill a third
        // slot with — so it is caught at composition rather than on the first step of a live instance.
        int arity = resolved.GetParameters().Length;
        if (arity is not (1 or 2))
        {
            throw new InvalidOperationException(
                $"'{typeof(I).Name}.{resolved.Name}' declares {arity} parameters; a governed step operation takes its step DTO, optionally followed by the event data a raise may carry");
        }

        return resolved;
    }

    /// <summary>The consumer's step-operation name, discovered from the contract.</summary>
    public string OperationName { get; }

    /// <summary>The serializer the host pipeline uses — reused for envelope/result bytes.</summary>
    public IMessageSerializer Serializer { get; }

    /// <summary>True when the governed operation declares a second parameter to receive raise-time event data.</summary>
    public bool AcceptsEventData { get; }

    /// <summary>
    /// Wraps a typed step DTO into the opaque durable envelope and seals it under the instance
    /// key (minting the key on first use). The bytes are ciphertext — the only form a backend ever
    /// journals — so the termination key destroy crypto-shreds the persisted payload.
    /// </summary>
    public byte[] SealStep(string instanceId, object stepDto, byte[]? ambientContext = null) =>
        _sealer.Seal(instanceId, stepDto, ambientContext);

    /// <summary>Seals raise-time event data for the governed operation's second parameter. See the interface member.</summary>
    public byte[] SealEventData(string instanceId, object eventData) =>
        _sealer.SealEventData(instanceId, eventData);

    /// <summary>Unseals a sealed envelope and returns the ambient bytes it carries (to flow onto the next step).</summary>
    public byte[]? AmbientOf(string instanceId, byte[] sealedEnvelope) =>
        WorkflowEnvelope.AmbientBytes(Serializer, _keys.Decrypt(instanceId, sealedEnvelope), typeof(I));

    /// <summary>The typed step DTO a sealed envelope carries — a native host threads carried state through the framework, not raw decryption.</summary>
    public T UnsealStep<T>(string instanceId, byte[] sealedEnvelope) =>
        WorkflowEnvelope.StepArg<T>(Serializer, _keys.Decrypt(instanceId, sealedEnvelope), typeof(I));

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

    /// <summary>Folds an action's declared subjects into the step's context. See the interface member.</summary>
    public byte[]? EnrollSubjects(string instanceId, byte[]? ambientContext, WorkflowAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        IReadOnlyList<string> declared = action.Subjects;
        if (declared is not { Count: > 0 })
        {
            return ambientContext;
        }

        if (action is WorkflowAction.Delay)
        {
            // A delay reuses the current step rather than sealing a new one, so an extended context would be
            // dropped the moment the driver re-reads the ambient off that same step. Refuse rather than index
            // the subject and silently lose the half that guards the journal. (No subject in the message —
            // this throw can reach a journal.)
            throw new InvalidOperationException(
                "a Delay cannot enroll subjects: it seals no next step for them to travel on — declare them on " +
                "the action that continues the flow");
        }

        // The bag is rebuilt from the incoming bytes rather than from scratch so any other ambient stop the
        // host flowed onto the step survives; only the subject stop is replaced.
        var bag = new AmbientContext(Serializer);
        if (ambientContext is { Length: > 0 })
        {
            bag.Deserialize(ambientContext);
        }

        // The declaring step's own context decides the flag: an externally-managed flow that learns a subject
        // still defers indexing to the consumer's system, as it does for the subject it started with. With no
        // subject stop at all there is nothing to defer to, so what the step declares is workflow-managed.
        bool managed = true;
        List<string> merged = [];
        if (bag.Contains<SubjectContext>())
        {
            SubjectContext existing = bag.Get<SubjectContext>();
            managed = existing.WorkflowManaged;
            merged.AddRange(existing.SubjectIds ?? []);
        }

        List<string> added = [];
        foreach (string subject in declared)
        {
            if (string.IsNullOrEmpty(subject))
            {
                throw new ArgumentException("a step cannot enroll an empty subject id", nameof(action));
            }

            if (!merged.Contains(subject, StringComparer.Ordinal))
            {
                merged.Add(subject);
                added.Add(subject);
            }
        }

        if (added.Count == 0)
        {
            // Every declared subject is already carried — re-declaring one across a loop is the ordinary case,
            // so it is a no-op rather than a reseal. Idempotent for the same reason AddEdge is.
            return ambientContext;
        }

        // Index NOW, not when the next step runs: the same managed-only rule InstanceGovernor applies per step,
        // applied at the moment the flow learned the subject. Until this edge exists the only record of that
        // person is inside a sealed continuation, where an erasure request cannot look.
        if (managed)
        {
            foreach (string subject in added)
            {
                _index.AddEdge(subject, instanceId);
            }
        }

        bag.SetOrReplace(new SubjectContext(merged, managed));
        return bag.Serialize();
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

    /// <summary>
    /// Runs a typed step and returns its typed business result. A native flow that awaited an event passes
    /// the sealed event data it received, and the step operation's second parameter receives it.
    /// </summary>
    public async Task<TResult> ExecuteAsync<TResult>(StepContext ctx, object stepDto, byte[]? sealedEventData = null) =>
        (TResult)(await ExecuteAsync(ctx, stepDto, sealedEventData))!;

    /// <summary>Runs a typed step and returns its business result as an object.</summary>
    public Task<object?> ExecuteAsync(StepContext ctx, object stepDto, byte[]? sealedEventData = null) =>
        DispatchGovernedAsync(
            SealStep(ctx.InstanceId, stepDto, ctx.AmbientContext), sealedEventData, ctx.InstanceId, ctx.Sequence);

    /// <summary>
    /// Unseal under the instance key, govern (key mint + subject indexing) then dispatch, returning the
    /// entrypoint's result object. With an idempotency store the effect applies once per triple and the
    /// recorded result is itself sealed, so the idempotency store never holds plaintext payload.
    /// </summary>
    public Task<object?> DispatchGovernedAsync(byte[] sealedEnvelope, string instanceId, long sequence) =>
        DispatchGovernedAsync(sealedEnvelope, null, instanceId, sequence);

    /// <summary>Unseal, merge any raise-time event data, govern, then dispatch. See the interface member.</summary>
    public async Task<object?> DispatchGovernedAsync(
        byte[] sealedEnvelope, byte[]? sealedEventData, string instanceId, long sequence)
    {
        try
        {
            object? result = await DispatchGovernedCoreAsync(sealedEnvelope, sealedEventData, instanceId, sequence);
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

    private async Task<object?> DispatchGovernedCoreAsync(
        byte[] sealedEnvelope, byte[]? sealedEventData, string instanceId, long sequence)
    {
        byte[] stepEnvelope = MergeEventData(_keys.Decrypt(instanceId, sealedEnvelope), sealedEventData, instanceId);
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

    // Folds raise-time event data into the step envelope BEFORE anything reads it, so metadata extraction,
    // governance, the idempotency key and the dispatch all see one ordinary step. Doing it here rather than
    // in each driver is what keeps the merge inside the governed pipeline: the data is decrypted under the
    // same instance key, and a component cannot receive it by any route that skipped the governance.
    private byte[] MergeEventData(byte[] stepEnvelope, byte[]? sealedEventData, string instanceId)
    {
        if (sealedEventData is not { Length: > 0 })
        {
            // No data to merge, but the envelope may still predate the event-data parameter — a continuation
            // journaled by an earlier build carries one argument where the operation now declares two. Only
            // operations that take event data can be short, so a one-parameter step pays nothing here.
            return AcceptsEventData ? WorkflowEnvelope.PadToArity(Serializer, stepEnvelope, typeof(I)) : stepEnvelope;
        }

        if (!AcceptsEventData)
        {
            // Fail loud rather than drop it. The raiser supplied data this flow has nowhere to put, and
            // running the continuation without it would be indistinguishable from success — the instance
            // would carry on having silently ignored the only thing the raise was for. The driver's catch
            // parks the instance with its key retained, so an operator re-drives it once the component
            // declares the parameter. Type and operation names only: the data itself is the plaintext the
            // seal exists to keep out of a journal.
            throw new InvalidOperationException(
                $"'{OperationName}' was raised at with event data but declares only its step parameter — give it a second parameter to receive the data, or raise the event bare");
        }

        object eventData = WorkflowEnvelope.EventDataArg(Serializer, _keys.Decrypt(instanceId, sealedEventData), typeof(I))
            ?? throw new InvalidOperationException(
                $"the event data raised at '{OperationName}' carried no argument to hand to the step");

        // Check the type at the merge as well as at the seal. The seal catches a raiser who named the wrong
        // type; this catches one who used the wrong SEAL — raising a step (SealStep) at a branch that declared
        // an OnEvent, where the step half is not theirs to supply. Without it that blob rides the event-data
        // slot down to the serializer, which rejects it as a slot/type mismatch naming neither the operation
        // nor what the caller should have done. Type names only; never the data.
        if (!_eventDataType!.IsInstanceOfType(eventData))
        {
            throw new InvalidOperationException(
                $"'{OperationName}' declares its event data as {_eventDataType.Name} but the raise carried a {eventData.GetType().Name}. " +
                "A branch that declared an OnEvent continuation runs that continuation, so a raise at it supplies DATA, not a step — " +
                $"seal it with {nameof(IGovernedStep.SealEventData)} rather than {nameof(IGovernedStep.SealStep)}.");
        }

        return WorkflowEnvelope.WithEventData(Serializer, stepEnvelope, eventData, typeof(I));
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

        // Read the response against the same contract and operation the endpoint wrote it with. A serializer
        // that binds declared types (the allow-listed ones) writes the return as its declared type, carrying no
        // type marker; reading that back through an untyped `object` slot yields the serializer's own loose node
        // instead of the result, which then fails to re-serialize when the result is journaled or scanned. The
        // stock open serializer is indifferent to the extra arguments, so this is symmetric for every pipeline.
        InvocationResponse response = Serializer.Deserialize<InvocationResponse>(responseBytes, typeof(I), OperationName)
            ?? throw new InvalidOperationException("the endpoint did not return an InvocationResponse");
        return response.Response;
    }
}
