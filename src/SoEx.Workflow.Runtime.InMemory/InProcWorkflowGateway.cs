using System.Collections.Concurrent;

namespace SoEx.Workflow.Runtime.InMemory;

/// <summary>
/// The <see cref="IWorkflowGateway"/> over the InProc runtime. The runtime itself is
/// per-instance with no registry, so the gateway owns one: <see cref="StartAsync"/> spins
/// up an <see cref="InMemoryWorkflowRuntime"/> + <see cref="WorkflowDriver{I}"/> for the
/// instance and tracks them; <see cref="RaiseEventAsync"/> routes to the tracked runtime
/// (buffering if the flow is not yet waiting, as the runtime always has). Completion is
/// observed via <see cref="CompletionAsync"/> — adapter-specific, like every backend's.
/// </summary>
public sealed class InProcWorkflowGateway<I>(
    GovernedStep<I> step, GovernedTermination termination, IGatewayAuthorizer? authorizer = null) : IWorkflowGateway
    where I : class
{
    private readonly ConcurrentDictionary<string, (InMemoryWorkflowRuntime Runtime, Task<byte[]> Completion)> _instances = new();

    public async Task StartAsync(string instanceId, byte[] sealedSeed)
    {
        if (authorizer is not null)
        {
            await authorizer.AuthorizeStartAsync(instanceId);
        }

        // A *running* instance owns its id — a duplicate start while it is live is the caller's error. A
        // *completed* one no longer does: its slot is freed here so the same logical id can be re-onboarded
        // (a fresh generation), instead of being wedged forever by a finished run.
        if (_instances.TryGetValue(instanceId, out var existing) && !existing.Completion.IsCompleted)
        {
            throw new InvalidOperationException($"workflow instance '{instanceId}' is already running");
        }

        var runtime = new InMemoryWorkflowRuntime(instanceId);
        Task<byte[]> completion = new WorkflowDriver<I>(runtime, step, termination).RunAsync(sealedSeed);
        _instances[instanceId] = (runtime, completion);   // replaces a completed entry; adds otherwise
    }

    public async Task RaiseEventAsync(string instanceId, string eventName, byte[]? sealedPayload = null, string? raiseId = null)
    {
        if (authorizer is not null)
        {
            await authorizer.AuthorizeRaiseEventAsync(instanceId, eventName);
        }

        await Instance(instanceId).Runtime.RaiseEventAsync(instanceId, eventName, sealedPayload ?? [], raiseId);
    }

    /// <summary>The instance's run-to-completion task (its PII-free serialized result).</summary>
    public Task<byte[]> CompletionAsync(string instanceId) => Instance(instanceId).Completion;

    /// <summary>Advances the instance's time-skipping clock so durable timers fire.</summary>
    public void Advance(string instanceId, TimeSpan by) => Instance(instanceId).Runtime.Advance(by);

    private (InMemoryWorkflowRuntime Runtime, Task<byte[]> Completion) Instance(string instanceId) =>
        _instances.TryGetValue(instanceId, out var instance)
            ? instance
            : throw new InvalidOperationException($"no workflow instance '{instanceId}' is running on this gateway");
}
