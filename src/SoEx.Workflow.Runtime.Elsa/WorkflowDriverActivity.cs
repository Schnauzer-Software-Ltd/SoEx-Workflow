using Elsa.Workflows;
using Elsa.Workflows.Models;
using SoEx.Workflow;

namespace SoEx.Workflow.Runtime.Elsa;

/// <summary>
/// Portable flow — Elsa's generic driver as a single custom activity. It runs the entrypoint
/// step loop directly (Elsa is checkpoint/resume, not replay, so side effects run once) and suspends on
/// an Elsa <b>bookmark</b> for each wait/timer, resuming the loop on the callback. The entrypoint's step
/// operation returns a <see cref="WorkflowAction"/>; saga state threads through the bookmark resume input.
/// The per-run governed-step core is held on the (reused, in-memory) instance. Use this or the native
/// flow (a registered Elsa workflow of governed steps + <see cref="GovernedTerminationActivity"/>), never both.
/// </summary>
public sealed class WorkflowDriverActivity : Activity
{
    private const string TimerBookmark = "__timer";
    private const string SequenceProperty = "soex:sequence";

    public required IGovernedStep Step { get; init; }
    public required GovernedTermination Termination { get; init; }
    public required string SagaInstanceId { get; init; }
    public required byte[] Seed { get; init; }

    /// <summary>The per-step failure policy: bounded retry, then park-before-shred. Defaults to the cross-runtime default.</summary>
    public WorkflowStepOptions Options { get; init; } = WorkflowStepOptions.Default;

    public byte[]? Result { get; private set; }

    private long _sequence;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        await DriveAsync(context, Seed);
    }

    private async ValueTask DriveAsync(ActivityExecutionContext context, byte[] current)
    {
        try
        {
            await StepLoopAsync(context, current);
        }
        catch (Exception error)
        {
            // Park-before-shred: a failed portable instance is quarantined with its key RETAINED — a transient
            // step failure must never crypto-shred the sealed journal (the old behaviour shredded on the first
            // blip). The suspend paths (wait/delay) return normally and Loop stays in the loop, so only a real
            // failure reaches here. Recovery is a re-drive or a deliberate terminate; idempotent if a Complete
            // already shredded (key gone → the coordinator no-ops).
            await Termination.QuarantineAsync(
                SagaInstanceId, new IdempotencyKey(SagaInstanceId, "terminal", _sequence), Options.EffectiveMaxAttempts, error);
            throw;
        }
    }

    private async ValueTask StepLoopAsync(ActivityExecutionContext context, byte[] current)
    {
        while (true)
        {
            long seq = _sequence++;
            byte[]? ambient = Step.AmbientOf(SagaInstanceId, current);

            WorkflowAction action;
            try
            {
                action = await DispatchWithRetryAsync(current, seq);
            }
            catch (Exception ex) when (!GovernedStepFailure.IsJournalSafe(Step, SagaInstanceId, ambient, ex))
            {
                // Elsa persists a faulted activity's exception message in clear, and it survives the shred, so a
                // step exception carrying a subject id is replaced before it reaches the catch in DriveAsync that
                // shreds and rethrows. A PII-free message propagates unchanged for diagnosability.
                throw new InvalidOperationException(GovernedStepFailure.WithheldMessage);
            }

            switch (action)
            {
                case WorkflowAction.Complete complete:
                    await Termination.TerminateAsync(SagaInstanceId, Step.KeyFor(current, SagaInstanceId, seq), TerminationTrigger.NaturalCompletion);
                    // The result is journaled in clear and escapes the shred, so it must not carry the subject.
                    Result = Step.GuardResultPiiFree(Step.Serializer.Serialize(complete.Result), ambient);
                    await context.CompleteActivityAsync();
                    return;

                case WorkflowAction.RaiseIntoNext raise:
                    current = Step.SealStep(SagaInstanceId, raise.NextStep, ambient);
                    continue;

                case WorkflowAction.WaitForEvent wait:
                    // The bookmark name is journaled in clear, so it must not carry the subject. The
                    // wait's OnEvent continuation is sealed now and journaled on the bookmark, so a
                    // event raised with no payload resumes into it (mirror of the timer's onTimeout).
                    string eventName = Step.GuardVisibleName(wait.EventName, ambient);
                    Suspend(context);
                    context.CreateBookmark(new CreateBookmarkArgs
                    {
                        BookmarkName = eventName,
                        Stimulus = eventName,
                        Callback = OnResume,
                        AutoBurn = true,
                        Metadata = new Dictionary<string, string> { ["onEvent"] = Convert.ToBase64String(wait.OnEvent is { } oe ? Step.SealStep(SagaInstanceId, oe, ambient) : []) },
                    });
                    if (wait.Timeout is { } timeout)
                    {
                        context.CreateBookmark(new CreateBookmarkArgs
                        {
                            BookmarkName = TimerBookmark,
                            Stimulus = $"{TimerBookmark}:{_sequence}",
                            Callback = OnResume,
                            AutoBurn = true,
                            Metadata = TimerMetadata(timeout, wait.OnTimeout is { } ot ? Step.SealStep(SagaInstanceId, ot, ambient) : []),
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
                    current = Step.SealStep(SagaInstanceId, loop.CarryState, ambient);
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
    private async ValueTask<WorkflowAction> DispatchWithRetryAsync(byte[] current, long seq)
    {
        int attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                return (await Step.DispatchGovernedAsync(current, SagaInstanceId, seq)) as WorkflowAction
                    ?? throw new InvalidOperationException($"the '{Step.OperationName}' operation did not return a {nameof(WorkflowAction)}");
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

    /// <summary>
    /// Persists the per-step sequence into the (durable) activity context before suspending. A resume may
    /// land on a rehydrated activity object whose <see cref="_sequence"/> field is fresh; without this the
    /// resumed loop would reuse spent sequence numbers and collide idempotency keys with already-run steps.
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

        string b64 = context.WorkflowInput.TryGetValue("payload", out object? value) ? value?.ToString() ?? "" : "";
        byte[] payload = string.IsNullOrEmpty(b64) ? [] : Convert.FromBase64String(b64);
        await DriveAsync(context, payload);
    }
}
