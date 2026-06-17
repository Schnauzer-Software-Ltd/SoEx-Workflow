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
        catch
        {
            // A failed portable instance still owes the crypto-shred — mirror of the native Elsa termination. The
            // suspend paths (wait/delay) return normally and Loop stays in the loop, so only a failure
            // reaches here; the idempotent termination no-ops if a Complete already shredded.
            await Termination.TerminateAsync(
                SagaInstanceId, new IdempotencyKey(SagaInstanceId, "terminal", _sequence), TerminationTrigger.NaturalCompletion);
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
                action = (await Step.DispatchGovernedAsync(current, SagaInstanceId, seq)) as WorkflowAction
                    ?? throw new InvalidOperationException($"the '{Step.OperationName}' operation did not return a {nameof(WorkflowAction)}");
            }
            catch (Exception ex) when (!GovernedStepFailure.IsJournalSafe(Step, ambient, ex))
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
                    if (wait.Timeout is not null)
                    {
                        context.CreateBookmark(new CreateBookmarkArgs
                        {
                            BookmarkName = TimerBookmark,
                            Stimulus = $"{TimerBookmark}:{_sequence}",
                            Callback = OnResume,
                            AutoBurn = true,
                            Metadata = new Dictionary<string, string> { ["onTimeout"] = Convert.ToBase64String(wait.OnTimeout is { } ot ? Step.SealStep(SagaInstanceId, ot, ambient) : []) },
                        });
                    }
                    return;

                case WorkflowAction.Delay:
                    Suspend(context);
                    context.CreateBookmark(new CreateBookmarkArgs
                    {
                        BookmarkName = TimerBookmark,
                        Stimulus = $"{TimerBookmark}:{_sequence}",
                        Callback = OnResume,
                        AutoBurn = true,
                        Metadata = new Dictionary<string, string> { ["onTimeout"] = Convert.ToBase64String(current) },
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
