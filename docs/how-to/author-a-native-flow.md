> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# How to author a native flow

In a native flow you author the flow in your runtime's own model (a Temporal `[Workflow]`, a Durable
Task orchestration, an Elsa graph, the Restate sidecar (`restate-sidecar-rs`), or a Camunda 8 BPMN
diagram) and call the governed step from each step and the governed termination at the end. This guide
gives the copy-pasteable shape for each runtime.

> First [write your step component](write-a-step-component.md) (it returns a business result) and
> [wire the governed core](../reference/governed-core.md) to get `step` and `termination`. For why the
> runtimes differ, see [Runtimes and durability](../explanation/runtimes-and-durability.md).

## Journal only the sealed seed

Because you author the flow, you decide what each step persists, and for crypto-shred to hold, the flow
must persist only ciphertext. The pattern:

1. Seal the subject once into an opaque seed (`step.SealStep(...)`), which mints the key.
2. Submit that seed as the workflow input and thread it through each step, naming only PII-free kinds.
3. Recover the subject in clear only inside a step (off the replay path), through the framework, with
   `step.UnsealStep<T>(...)` / `step.AmbientOf(...)`. Never pass a plaintext DTO or ambient bytes as an
   activity argument.

These shared pieces are reused by every backend snippet below:

```csharp
// What the flow threads between steps: an opaque sealed seed + a PII-free kind.
public sealed record SealedStep(byte[] Seed, string InstanceId, long Seq);
public sealed record NativeInput(byte[] Seed);

public static class Native
{
    // Seal the subject once (mints the key). The only subject-bearing thing the flow or backend sees.
    public static byte[] SealSeed(GovernedStep<IOnboardSteps> step, string instanceId, string email)
        => step.SealStep(instanceId, new OnbStep.Lookup(email),
                         WorkflowEnvelope.AmbientFor(step.Serializer, SubjectContext.Managed(email)));

    // Called inside a step (off the replay path): recover the subject through the framework, build the
    // kind's DTO, run the governed step. Returns a PII-free outcome.
    public static Task<StepOutcome> RunSealed(
        GovernedStep<IOnboardSteps> step, string id, long seq, string kind, byte[] seed)
    {
        string email = step.UnsealStep<OnbStep.Lookup>(id, seed).Email;          // in memory only
        OnbStep dto = kind switch
        {
            "lookup" => new OnbStep.Lookup(email),
            "invite" => new OnbStep.Invite(email, "res-1"),
            "assign" => new OnbStep.Assign("res-1", "user"),
            _        => throw new ArgumentException($"unknown kind '{kind}'"),
        };
        return step.ExecuteAsync<StepOutcome>(new StepContext(id, seq, step.AmbientOf(id, seed)), dto);
    }
}

// Submit: seal the seed, then start the backend's flow with only that ciphertext.
string instanceId = "onb-" + Guid.NewGuid().ToString("N");
byte[] seed = Native.SealSeed(step, instanceId, "invitee@example.com");
```

The result and any event/timer names stay PII-free by your construction; must-retain PII leaves via
`OnRetaining`. (`IRetainedStore` in the examples is just your own durable sink.)

## Durable Task

Extend `GovernedTaskOrchestrator<TIn,TOut>` (its termination hook runs `GovernedTerminationActivity`
at completion) and author the sequence with `CallActivity`/`WaitForExternalEvent`. Register the
orchestrator, `GovernedTerminationActivity`, and your step activities on the worker; register
`termination` in DI.

```csharp
public sealed class NativeOnboard : GovernedTaskOrchestrator<NativeInput, string>
{
    protected override async Task<string> Flow(TaskOrchestrationContext context, NativeInput input)
    {
        await context.CallActivityAsync<StepOutcome>("Lookup", new SealedStep(input.Seed, context.InstanceId, 0));
        await context.WaitForExternalEvent<bool>("accept");
        await context.CallActivityAsync<StepOutcome>("Assign", new SealedStep(input.Seed, context.InstanceId, 1));
        return "assigned";   // PII-free; the base orchestrator's finally runs GovernedTerminationActivity
    }
}
// activity "Lookup": (SealedStep c) => Native.RunSealed(step, c.InstanceId, c.Seq, "lookup", c.Seed)
```

## Temporal

Author a normal `[Workflow]` with its own sequence, `WaitConditionAsync`, and timeout. Each step is an
`[Activity]` that calls `step.ExecuteAsync`. Register the `GovernedTerminationInterceptor` on the worker
(it schedules `GovernedTerminationActivities.RunTermination` in a `finally`, off the replay path) and
register `termination` in DI.

```csharp
[Workflow]
public class NativeOnboard
{
    private bool _accepted;

    [WorkflowRun]
    public async Task<string> Run(NativeInput input)
    {
        await Wf.ExecuteActivityAsync((GovernedSteps a) => a.Lookup(input.Seed, 0), opts);
        await Wf.WaitConditionAsync(() => _accepted, TimeSpan.FromSeconds(input.TimeoutSeconds));
        await Wf.ExecuteActivityAsync((GovernedSteps a) => a.Assign(input.Seed, 1), opts);
        return "assigned";
    }

    [WorkflowSignal] public Task Accept() { _accepted = true; return Task.CompletedTask; }
}
// GovernedSteps.Lookup(byte[] seed, long seq) => Native.RunSealed(step, Wf.Info.WorkflowId, seq, "lookup", seed)
```

The termination must run only via the interceptor's activity. The key-store mutation is
non-deterministic and has to stay off the replay path.

## Elsa

Author a registered Elsa workflow: each governed step is an activity that calls `step.ExecuteAsync`,
waits are bookmarks, and the flow ends in `GovernedTerminationActivity`. For a durable host, resolve
`GovernedStep`/`GovernedTermination` from DI inside the activities so a rehydrated instance on a fresh
host gets them.

```csharp
var workflow = new Workflow
{
    Root = new Sequence { Activities =
    {
        new GovStep { Step = step, Kind = "lookup", Seq = 0, Seed = seed },
        new WaitEvent { EventName = "invite-accepted" },
        new GovStep { Step = step, Kind = "assign", Seq = 1, Seed = seed },
        new GovernedTerminationActivity { Termination = termination },
    } },
};
// Run with RunWorkflowOptions.WorkflowInstanceId equal to the id you sealed the seed under, so the
// governed steps and the termination anchor on the same id.
// GovStep.ExecuteAsync => Native.RunSealed(Step, ctx.WorkflowExecutionContext.Id, Seq, Kind, Seed)
```

## Restate (cross-language)

Restate has no .NET SDK, so you author the flow natively in the sidecar's language, Rust. The Restate
sidecar drives the sequence, the durable-promise wait, and the termination, calling back to a small
.NET governed-step host over HTTP: `POST /gov-step` runs `step.ExecuteAsync`, and `POST /gov-terminate`
runs `termination.TerminateAsync`. See the
[Restate adapter README](../../src/SoEx.Workflow.Runtime.Restate/README.md).

## Camunda 8 / Zeebe (visual BPMN)

The broker owns the flow, so you author it as a BPMN diagram in a visual editor (Camunda Modeler or
BPMN-js) and deploy the `.bpmn`. Each governed step is a service task whose job a worker handles, with
its PII-free kind and sequence riding as static task headers; waits are message-catch events correlated
on the instance id; the termination is a process end execution-listener job. The .NET side:

```csharp
IZeebeClient client = ZeebeWorkflowHost.Connect("127.0.0.1:26500");
// DeployAsync lints the io-mappings as it deploys and RETURNS any warnings — inspect/act on them.
var warnings = await ZeebeWorkflowHost.DeployAsync(client, "bpmn/onboard.bpmn");   // the visual-editor artifact

using var steps = ZeebeWorkflowHost.OpenStepWorker(client, "onboard-step", step,
    async (id, seq, kind, seed) => await Native.RunSealed(step, id, seq, kind, seed));   // unseal + dispatch
using var term = ZeebeWorkflowHost.OpenTerminationListener(client, "onboard-terminal", step, termination);  // shred at end

var gateway = new ZeebeWorkflowGateway(client, "onboard");                 // the BPMN process id
await gateway.StartAsync(instanceId, seed);                               // seed + id ride as process variables
await gateway.RaiseEventAsync(instanceId, "invite-accepted");            // a correlated Zeebe message
```

The framework writes exactly two process variables: the sealed seed and the PII-free instance id. It
can't police a consumer's own BPMN io-mappings, so `DeployAsync` lints each resource as it deploys and
returns the findings (it calls `ZeebeWorkflowHost.ValidateResource` internally — you can also run that
standalone): each `ZeebeResourceWarning` flags a governed task that copies `seed`/`instanceId` into
another journaled variable. The warnings are advisory and deployment proceeds regardless, so inspect the
returned list and decide whether to act or block. Camunda 8 / Zeebe is native-only, since a
`WorkflowAction` loop isn't expressed on a BPMN graph.

## Reference

- The [runtime matrix](../reference/runtime-matrix.md) — exactly how each concept maps to each backend.
- [Runtimes and durability](../explanation/runtimes-and-durability.md) — the durability models and why
  the termination must stay off the replay path.
