using Elsa.Workflows;
using Microsoft.Extensions.DependencyInjection;
using SoEx.Workflow;

namespace PiiMaker.Host.Elsa;

/// <summary>The termination hook as an Elsa activity: crypto-shred + prune, anchored on the correlation id.</summary>
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
