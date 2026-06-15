using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Models;
using Elsa.Workflows.Options;
using Microsoft.Extensions.DependencyInjection;
using SoEx.Workflow;
using ElsaWorkflow = Elsa.Workflows.Activities.Workflow;

namespace SoEx.Workflow.Elsa;

/// <summary>
/// Test host — runs the portable flow on Elsa 3 with the <b>in-memory</b> provider (no persistence; nothing
/// survives a restart). Drives the bookmark resume loop: each suspended wait is resumed either by a delivered
/// event (matched by bookmark name) or, for the durable timer, when the test advances time. For durable
/// hosting against a persistence provider use <see cref="ElsaWorkflowHost"/>. Use this or the native
/// flow, never both.
/// </summary>
public sealed class ElsaTestWorkflowHost
{
    private static readonly IServiceProvider Services = BuildServices();

    private static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddElsa();
        return services.BuildServiceProvider();
    }

    public ElsaRun Start(
        string instanceId, IGovernedStep step, GovernedTermination termination, byte[] seed,
        IReadOnlyDictionary<string, byte[]>? prearmedEvents)
    {
        // The instance id and event names are journaled in clear, so they must not carry the subject.
        byte[]? ambient = step.AmbientOf(instanceId, seed);
        step.GuardVisibleName(instanceId, ambient);
        foreach (string name in (prearmedEvents ?? new Dictionary<string, byte[]>()).Keys)
        {
            step.GuardVisibleName(name, ambient);
        }

        var driver = new WorkflowDriverActivity
        {
            Step = step,
            Termination = termination,
            SagaInstanceId = instanceId,
            Seed = seed,
        };
        var workflow = new ElsaWorkflow { Root = driver };
        var advanceGate = new TaskCompletionSource();

        Task<byte[]> completion = DriveAsync(workflow, driver, prearmedEvents ?? new Dictionary<string, byte[]>(), advanceGate);
        return new ElsaRun(completion, () => advanceGate.TrySetResult());
    }

    private static async Task<byte[]> DriveAsync(
        ElsaWorkflow workflow, WorkflowDriverActivity driver,
        IReadOnlyDictionary<string, byte[]> prearmed, TaskCompletionSource advanceGate)
    {
        var runner = Services.GetRequiredService<IWorkflowRunner>();
        var result = await runner.RunAsync(workflow);
        ThrowIfFaulted(result);

        while (driver.Result is null)
        {
            List<Bookmark> bookmarks = result.WorkflowState.Bookmarks.ToList();
            if (bookmarks.Count == 0)
            {
                break;
            }

            Bookmark target;
            byte[] payload;

            Bookmark? bookmark = bookmarks.FirstOrDefault(b => prearmed.ContainsKey(b.Name));
            if (bookmark is not null)
            {
                target = bookmark;
                payload = ElsaEventPayload.Resolve(bookmark, prearmed[bookmark.Name]);
            }
            else
            {
                Bookmark? timer = bookmarks.FirstOrDefault(b => b.Name == "__timer");
                if (timer is null)
                {
                    throw new InvalidOperationException("Elsa: no resumable bookmark (stuck)");
                }

                await advanceGate.Task;   // the durable timer fires when the test advances time
                target = timer;
                payload = timer.Metadata is { } md && md.TryGetValue("onTimeout", out string? onTimeout) && !string.IsNullOrEmpty(onTimeout)
                    ? Convert.FromBase64String(onTimeout)
                    : [];
            }

            result = await runner.RunAsync(workflow, result.WorkflowState, new RunWorkflowOptions
            {
                BookmarkId = target.Id,
                Input = new Dictionary<string, object> { ["payload"] = Convert.ToBase64String(payload) },
            });
            ThrowIfFaulted(result);
        }

        return driver.Result ?? [];
    }

    /// <summary>
    /// Elsa absorbs an activity exception into a workflow incident rather than letting it propagate, which
    /// would let a faulted run (e.g. a rejected subject-bearing name or result) look like an empty success.
    /// Rethrow it so enforcement surfaces to the caller exactly as on the other runtimes.
    /// </summary>
    private static void ThrowIfFaulted(RunWorkflowResult result)
    {
        var incident = result.WorkflowState.Incidents.FirstOrDefault();
        if (incident is not null)
        {
            throw new InvalidOperationException($"Elsa: the workflow faulted: {incident.Message}");
        }
    }
}

public sealed class ElsaRun(Task<byte[]> completion, Action advance)
{
    public Task<byte[]> Completion => completion;

    public void Advance() => advance();
}
