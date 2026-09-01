using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Models;
using SoEx.Workflow;

namespace SoEx.Workflow.Runtime.Elsa;

/// <summary>
/// Portable flow — Elsa's generic driver as a single custom activity. It runs the entrypoint
/// step loop directly (Elsa is checkpoint/resume, not replay, so side effects run once) and suspends on
/// an Elsa <b>bookmark</b> for each wait/timer, resuming the loop on the callback. The entrypoint's step
/// operation returns a <see cref="WorkflowAction"/>; saga state threads through the bookmark resume input.
/// Use this or the native flow (a registered Elsa workflow of governed steps +
/// <see cref="GovernedTerminationActivity"/>), never both.
/// <para>
/// The governed core and the per-instance values are resolved <b>per run</b> unless supplied on the activity.
/// That is what lets the driver sit in a <b>registered</b> definition (<c>module.AddWorkflow&lt;T&gt;()</c>):
/// a registered definition is built once, with no access to run input, and a rehydrated instance on a fresh
/// host holds no live object references at all. So <see cref="Step"/>/<see cref="Termination"/> fall back to
/// DI (<see cref="ElsaWorkflowHost.BuildDurable"/> registers both), the saga id falls back to the Elsa
/// <b>correlation id</b> (the id governance is anchored on, which <see cref="ElsaWorkflowGateway"/> sets at
/// start), and the seed falls back to the <c>seed</c> workflow input. Supplying them explicitly — as
/// <see cref="ElsaTestWorkflowHost"/> does for a per-run driver on the in-memory host — keeps the old
/// behaviour unchanged.
/// </para>
/// </summary>
public sealed class WorkflowDriverActivity : Activity
{
    private const string TimerBookmark = "__timer";
    private const string SequenceProperty = "soex:sequence";

    /// <summary>The workflow input carrying the base64 sealed seed when <see cref="Seed"/> is not supplied.</summary>
    public const string SeedInput = "seed";

    /// <summary>The workflow variable the completed (PII-free) result is published to, for a registered definition.</summary>
    public const string ResultVariable = "soex:result";

    /// <summary>The governed step. Optional: falls back to the <see cref="IGovernedStep"/> registered in DI.</summary>
    public IGovernedStep? Step { get; init; }

    /// <summary>The governed termination. Optional: falls back to the <see cref="GovernedTermination"/> registered in DI.</summary>
    public GovernedTermination? Termination { get; init; }

    /// <summary>The logical saga id governance anchors on. Optional: falls back to the Elsa correlation id.</summary>
    public string? SagaInstanceId { get; init; }

    /// <summary>The sealed start state. Optional: falls back to the base64 <c>seed</c> workflow input.</summary>
    public byte[]? Seed { get; init; }

    /// <summary>The per-step failure policy: bounded retry, then park-before-shred. Defaults to the cross-runtime default.</summary>
    public WorkflowStepOptions Options { get; init; } = WorkflowStepOptions.Default;

    /// <summary>
    /// The completed result, for a host that holds this driver instance across the whole run (see
    /// <see cref="ElsaTestWorkflowHost"/>). A registered definition materialises a fresh activity object per
    /// run, so nothing can read this field there — the same value is published to the
    /// <see cref="ResultVariable"/> workflow variable, which is durable and survives the run.
    /// </summary>
    public byte[]? Result { get; private set; }

    private long _sequence;

    /// <summary>The governed core plus the saga id for one run, resolved from the activity or the run context.</summary>
    private readonly record struct Bound(IGovernedStep Step, GovernedTermination Termination, string SagaInstanceId);

    private Bound Resolve(ActivityExecutionContext context) => new(
        Step ?? context.GetRequiredService<IGovernedStep>(),
        Termination ?? context.GetRequiredService<GovernedTermination>(),
        SagaInstanceId ?? (context.WorkflowExecutionContext.CorrelationId is { Length: > 0 } correlation
            ? correlation
            : throw new InvalidOperationException(
                $"the portable Elsa driver has no saga id: set {nameof(SagaInstanceId)} on the activity, or start the "
                + "instance with the logical id as the Elsa correlation id (which ElsaWorkflowGateway.StartAsync does)")));

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        await DriveAsync(context, Resolve(context), Seed ?? DecodeInput(context, SeedInput));
    }

    private async ValueTask DriveAsync(ActivityExecutionContext context, Bound bound, byte[] current)
    {
        try
        {
            await StepLoopAsync(context, bound, current);
        }
        catch (Exception error)
        {
            // Park-before-shred: a failed portable instance is quarantined with its key RETAINED — a transient
            // step failure must never crypto-shred the sealed journal (the old behaviour shredded on the first
            // blip). The suspend paths (wait/delay) return normally and Loop stays in the loop, so only a real
            // failure reaches here. Recovery is a re-drive or a deliberate terminate; idempotent if a Complete
            // already shredded (key gone → the coordinator no-ops).
            await bound.Termination.QuarantineAsync(
                bound.SagaInstanceId, new IdempotencyKey(bound.SagaInstanceId, "terminal", _sequence),
                Options.EffectiveMaxAttempts, error);
            throw;
        }
    }

    private async ValueTask StepLoopAsync(ActivityExecutionContext context, Bound bound, byte[] current)
    {
        (IGovernedStep step, _, string sagaInstanceId) = bound;

        while (true)
        {
            long seq = _sequence++;
            byte[]? ambient = step.AmbientOf(sagaInstanceId, current);

            WorkflowAction action;
            try
            {
                action = await DispatchWithRetryAsync(bound, current, seq);
            }
            catch (Exception ex) when (!GovernedStepFailure.IsJournalSafe(step, sagaInstanceId, ambient, ex))
            {
                // Elsa persists a faulted activity's exception message in clear, and it survives the shred, so a
                // step exception carrying a subject id is replaced before it reaches the catch in DriveAsync that
                // shreds and rethrows. A PII-free message propagates unchanged for diagnosability.
                throw new InvalidOperationException(GovernedStepFailure.WithheldMessage);
            }

            switch (action)
            {
                case WorkflowAction.Complete complete:
                    await bound.Termination.TerminateAsync(
                        sagaInstanceId, step.KeyFor(current, sagaInstanceId, seq), TerminationTrigger.NaturalCompletion);
                    // The result is journaled in clear and escapes the shred, so it must not carry the subject.
                    Result = step.GuardResultPiiFree(step.Serializer.Serialize(complete.Result), ambient);
                    // Published durably too: a registered definition discards this activity object after the run.
                    context.SetVariable(ResultVariable, Convert.ToBase64String(Result));
                    await context.CompleteActivityAsync();
                    return;

                case WorkflowAction.RaiseIntoNext raise:
                    current = step.SealStep(sagaInstanceId, raise.NextStep, ambient);
                    continue;

                case WorkflowAction.WaitForEvent wait:
                    // One bookmark per branch, each carrying its OWN sealed OnEvent continuation. The bookmark
                    // names are journaled in clear, so every branch name is guarded (WaitBranches.Flatten does
                    // both). The resume path needs no branch selection: ElsaEventPayload.Resolve reads the
                    // continuation from the bookmark the gateway actually targets, and OnResume's
                    // ClearBookmarks() burns the losing branches — so exactly one branch can ever resume a wait.
                    Suspend(context);
                    foreach (WaitBranchWire branch in WaitBranches.Flatten(step, sagaInstanceId, wait, ambient))
                    {
                        context.CreateBookmark(new CreateBookmarkArgs
                        {
                            BookmarkName = branch.EventName,
                            Stimulus = branch.EventName,
                            Callback = OnResume,
                            AutoBurn = true,
                            Metadata = new Dictionary<string, string> { ["onEvent"] = Convert.ToBase64String(branch.OnEvent) },
                        });
                    }

                    if (wait.Timeout is { } timeout)
                    {
                        context.CreateBookmark(new CreateBookmarkArgs
                        {
                            BookmarkName = TimerBookmark,
                            Stimulus = $"{TimerBookmark}:{_sequence}",
                            Callback = OnResume,
                            AutoBurn = true,
                            Metadata = TimerMetadata(timeout, wait.OnTimeout is { } ot ? step.SealStep(sagaInstanceId, ot, ambient) : []),
                        });
                    }

                    return;

                case WorkflowAction.Delay delay:
                    Suspend(context);
                    context.CreateBookmark(new CreateBookmarkArgs
                    {
                        BookmarkName = TimerBookmark,
                        Stimulus = $"{TimerBookmark}:{_sequence}",
                        Callback = OnResume,
                        AutoBurn = true,
                        Metadata = TimerMetadata(delay.Duration, current),
                    });
                    return;

                case WorkflowAction.Loop loop:
                    current = step.SealStep(sagaInstanceId, loop.CarryState, ambient);
                    continue;

                default:
                    throw new InvalidOperationException($"unhandled action: {action.Kind()}");
            }
        }
    }

    // Runs one governed step under the binding's failure policy: retry the dispatch on a non-terminal failure
    // with bounded exponential backoff, up to MaxAttempts. When the attempts are spent (or the failure is
    // terminal) the exception propagates to the journal-safety scrub and then to DriveAsync's catch, which
    // parks the instance. Retry backoff is a real in-execution wait; a redelivered activity re-enters fresh,
    // and the step's idempotency (when wired) collapses a re-run that already recorded its effect.
    private async ValueTask<WorkflowAction> DispatchWithRetryAsync(Bound bound, byte[] current, long seq)
    {
        int attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                return (await bound.Step.DispatchGovernedAsync(current, bound.SagaInstanceId, seq)) as WorkflowAction
                    ?? throw new InvalidOperationException($"the '{bound.Step.OperationName}' operation did not return a {nameof(WorkflowAction)}");
            }
            catch (Exception ex) when (Options.ShouldRetry(attempt, ex))
            {
                await Task.Delay(Options.DelayBefore(attempt));
            }
        }
    }

    // The __timer bookmark carries its DUE TIME (dueAt) so a consumer-driven resumer can find and fire due
    // timers on the wall clock — the framework does not host a scheduler for Elsa, so a production Elsa host
    // scans due __timer bookmarks and resumes them (see the runtime matrix / operate-in-production guide). Elsa
    // is checkpoint/resume (not replay), so reading the wall clock here is deterministic-safe. onTimeout is the
    // sealed step to resume into when the timer fires.
    private static Dictionary<string, string> TimerMetadata(TimeSpan after, byte[] onTimeoutSealed) => new()
    {
        ["dueAt"] = DateTimeOffset.UtcNow.Add(after).ToString("O"),
        ["onTimeout"] = Convert.ToBase64String(onTimeoutSealed),
    };

    private static byte[] DecodeInput(ActivityExecutionContext context, string key)
    {
        string b64 = context.WorkflowInput.TryGetValue(key, out object? value) ? value?.ToString() ?? "" : "";
        return string.IsNullOrEmpty(b64) ? [] : Convert.FromBase64String(b64);
    }

    /// <summary>
    /// Persists the per-step sequence into the (durable) activity context before suspending. A resume lands on
    /// a rehydrated activity object whose <see cref="_sequence"/> field is fresh; without this the resumed loop
    /// would reuse spent sequence numbers and collide idempotency keys with already-run steps.
    /// </summary>
    private void Suspend(ActivityExecutionContext context) =>
        context.Properties[SequenceProperty] = _sequence.ToString();

    private async ValueTask OnResume(ActivityExecutionContext context)
    {
        context.ClearBookmarks();
        if (context.Properties.TryGetValue(SequenceProperty, out object? persisted) && long.TryParse(persisted?.ToString(), out long sequence))
        {
            _sequence = sequence;
        }

        await DriveAsync(context, Resolve(context), DecodeInput(context, "payload"));
    }
}
