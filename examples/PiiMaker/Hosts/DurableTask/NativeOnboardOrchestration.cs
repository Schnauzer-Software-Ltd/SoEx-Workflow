using Microsoft.DurableTask;
using PiiMaker.Manager.Membership.Interface;
using SoEx.Workflow.Runtime.DurableTask;

namespace PiiMaker.Host.DurableTask;

/// <summary>
/// A — onboarding: a consumer-authored sequence of governed steps with a wait-for-accept (timeout falls
/// through to a no-op completion — the happy path raises the event). The base orchestrator shreds at the end.
/// </summary>
public sealed class NativeOnboardOrchestration : GovernedTaskOrchestrator<NativeInput, string>
{
    protected override async Task<string> Flow(TaskOrchestrationContext context, NativeInput input)
    {
        var retry = new TaskOptions(new TaskRetryOptions(new RetryPolicy(5, TimeSpan.FromMilliseconds(50))));
        string id = context.InstanceId;

        await context.CallActivityAsync<StepReceipt>("Lookup", new SealedStep(input.Seed, id, 0));
        await context.CallActivityAsync<StepReceipt>("Create", new SealedStep(input.Seed, id, 1), retry);
        await context.CallActivityAsync<StepReceipt>("Reserve", new SealedStep(input.Seed, id, 2));
        await context.CallActivityAsync<StepReceipt>("Invite", new SealedStep(input.Seed, id, 3));

        try
        {
            // The gateway delivers a RaisedEvent wrapper (an optional raise id + the sealed payload bytes);
            // a bare raise carries an empty payload, which still resumes this wait.
            await context.WaitForExternalEvent<RaisedEvent>("invite-accepted", TimeSpan.FromSeconds(input.TimeoutSeconds));
        }
        catch (TaskCanceledException)
        {
            return "invite-timed-out";   // the durable timer won the race
        }

        await context.CallActivityAsync<StepReceipt>("Assign", new SealedStep(input.Seed, id, 4));
        return "assigned";
    }
}
