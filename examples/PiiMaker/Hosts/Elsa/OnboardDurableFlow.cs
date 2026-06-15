using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Microsoft.Extensions.DependencyInjection;
using PiiMaker.Manager.Membership.Interface;
using PiiMaker.Manager.Membership.Service;
using SoEx.Workflow;

namespace PiiMaker.Host.Elsa;

// The native Elsa onboarding flow as a REGISTERED workflow, so a fresh host rebuilds the identical
// definition and resumes the persisted bookmark. A rehydrated instance can carry no live object
// references, so each activity resolves GovernedStep/GovernedTermination from DI and reads the sealed seed
// from the workflow input. Governance is anchored on the correlation id (which the host controls and
// sealed the seed under).

/// <summary>A governed onboarding step: recover the subject through the framework, run the OnboardStep operation.</summary>
public sealed class GovStep : Activity
{
    public string Kind { get; set; } = "";
    public long Seq { get; set; }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var step = context.GetRequiredService<GovernedStep<IMembershipManager>>();
        string anchor = context.WorkflowExecutionContext.CorrelationId!;
        // The sealed seed arrives as workflow input at start, then persists as a workflow VARIABLE so
        // steps after a bookmark resume still see it — a context-free resume carries no input of its own.
        string b64 = context.GetVariable<string>("seed") is { Length: > 0 } persisted
            ? persisted
            : (string)context.WorkflowInput["seed"]!;
        context.SetVariable("seed", b64);
        await MembershipNative.RunOnboardStep(step, anchor, Seq, Kind, Convert.FromBase64String(b64));
        await context.CompleteActivityAsync();
    }
}

/// <summary>A native Elsa wait: suspend on a bookmark, resume when the host delivers the named event.</summary>
public sealed class WaitEvent : Activity
{
    public string EventName { get; set; } = "";

    protected override void Execute(ActivityExecutionContext context) =>
        context.CreateBookmark(new CreateBookmarkArgs { BookmarkName = EventName, Stimulus = EventName, Callback = Resume, AutoBurn = true });

    private async ValueTask Resume(ActivityExecutionContext context) => await context.CompleteActivityAsync();
}

/// <summary>The termination hook: crypto-shred + prune, anchored on the correlation id.</summary>
public sealed class GovTermination : Activity
{
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var termination = context.GetRequiredService<GovernedTermination>();
        string anchor = context.WorkflowExecutionContext.CorrelationId!;
        await termination.TerminateAsync(anchor, new IdempotencyKey(anchor, "terminal", 0), TerminationTrigger.NaturalCompletion);
        await context.CompleteActivityAsync();
    }
}

/// <summary>Lookup → Reserve → SendInvite → wait(invite-accepted) → Assign → termination. Registered for durable resume.</summary>
public class MembershipOnboardWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder) => builder.Root = new Sequence
    {
        // the sealed seed's persisted home across suspensions (GovStep copies it from the start input);
        // workflow storage, not the default memory driver — it must survive a host restart
        Variables = { new Variable<string>("seed", "").WithWorkflowStorage() },
        Activities =
        {
            new GovStep { Kind = "lookup", Seq = 0 },
            new GovStep { Kind = "reserve", Seq = 1 },
            new GovStep { Kind = "invite", Seq = 2 },
            new WaitEvent { EventName = "invite-accepted" },
            new GovStep { Kind = "assign", Seq = 3 },
            new GovTermination(),
        },
    };
}
