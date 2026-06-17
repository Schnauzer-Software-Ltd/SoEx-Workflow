using Elsa.Extensions;
using Elsa.Workflows;
using Microsoft.Extensions.DependencyInjection;
using PiiMaker.Hosting;
using Native = PiiMaker.Manager.Membership.Interface.Native;
using SoEx.Workflow;

namespace PiiMaker.Host.Elsa;

/// <summary>
/// A governed onboarding step as a registered Elsa activity: recover the subject through the framework, run
/// the OnboardStep operation. A rehydrated instance carries no live object references, so it resolves the
/// <see cref="GovernedStep{I}"/> from DI and reads the sealed seed from the workflow variable (anchored on
/// the correlation id the host sealed under).
/// </summary>
public sealed class GovStep : Activity
{
    public string Kind { get; set; } = "";
    public long Seq { get; set; }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var step = context.GetRequiredService<GovernedStep<Native.IMembershipManager>>();
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
