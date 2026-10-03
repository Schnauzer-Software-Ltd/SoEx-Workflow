> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# How to author a native flow

In a native flow, you write the flow in the model of your runtime. Each step of the flow calls the
governed step. The end of the flow calls the governed termination. The native models are:

- a Temporal `[Workflow]`
- a Durable Task orchestration
- an Elsa graph
- the Restate sidecar (`restate-sidecar-rs`)
- a Camunda 8 BPMN diagram

This guide gives the code shape for each runtime. You can copy it into your project.

> Before you start, [write your step component](write-a-step-component.md). It returns a business
> result. Then [wire the governed core](../reference/governed-core.md) to get `step` and `termination`.
> [Runtimes and durability](../explanation/runtimes-and-durability.md) explains the differences between
> the runtimes.

## Journal only the sealed seed

In a native flow, your flow sets what each step persists. Crypto-shred protects only sealed data. Thus
the flow must persist only ciphertext. Use this procedure:

1. Seal the subject one time into an opaque seed with `step.SealStep(...)`.

   This call mints the per-instance key.

2. Submit the seed as the workflow input.
3. Pass the seed through each step, with a PII-free kind name for each step.

   PII is personally identifiable information.

4. Recover the subject in clear text only inside a step, with `step.UnsealStep<T>(...)` or
   `step.AmbientOf(...)`.

   The step runs off the replay path. The framework does the unseal.

5. Do not pass a plaintext DTO or ambient bytes as an activity argument.

Each runtime snippet in this guide uses these shared parts:

```csharp
// What the flow threads between steps: an opaque sealed seed + a PII-free kind.
public sealed record SealedStep(byte[] Seed, string InstanceId, long Seq);
public sealed record NativeInput(byte[] Seed, int TimeoutSeconds);

public static class Native
{
    // Seal the subject once (mints the key). The only subject-bearing thing the flow or backend sees.
    public static byte[] SealSeed(GovernedStep<IOnboardManager> step, string instanceId, string email)
        => step.SealStep(instanceId, new OnbStep.Lookup(email),
                         WorkflowEnvelope.AmbientFor(step.Serializer, SubjectContext.Managed(email)));

    // Called inside a step (off the replay path): recover the subject through the framework, build the
    // kind's DTO, run the governed step. Returns a PII-free outcome.
    public static Task<StepOutcome> RunSealed(
        GovernedStep<IOnboardManager> step, string id, long seq, string kind, byte[] seed)
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

You must keep the result, the event names, and the timer names free of PII. Your flow design does this.
PII that you must keep goes out through `OnRetaining`. In the examples, `IRetainedStore` is your own
durable store.

## Durable Task

1. Extend `GovernedTaskOrchestrator<TIn,TOut>`.

   Its termination hook runs `GovernedTerminationActivity` when the orchestration completes.

2. Write the sequence with `CallActivity` and `WaitForExternalEvent`.
3. Register the orchestrator on the worker.
4. Register `GovernedTerminationActivity` on the worker.
5. Register your step activities on the worker.
6. Register `termination` in DI.

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

1. Write a standard `[Workflow]` with its own sequence, `WaitConditionAsync`, and timeout.
2. Make each step an `[Activity]` that calls `step.ExecuteAsync`.
3. Register the `GovernedTerminationInterceptor` on the worker.

   The interceptor schedules `GovernedTerminationActivities.RunTermination` when the workflow completes.
   It also schedules it when the workflow fails or is cancelled. It does not schedule it on continue-as-new.
   The termination runs as an activity, off the replay path.

4. Register `termination` in DI.

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

> [!CAUTION]
> Run the termination only through the activity of the interceptor. The termination changes the key
> store. This change is non-deterministic, so it must stay off the replay path.

## Elsa

1. Write a registered Elsa workflow.
2. Make each governed step an activity that calls `step.ExecuteAsync`.
3. Make each wait a bookmark.
4. End the flow with `GovernedTerminationActivity`.
5. For a durable host, resolve `GovernedStep` and `GovernedTermination` from DI inside the activities.

   A rehydrated instance on a new host then gets them. If you leave `Termination` unset,
   `GovernedTerminationActivity` resolves it from DI itself.

6. Anchor each step and the termination on the **same id that you sealed under**.

   This id is the correlation id. `ElsaWorkflowGateway` sets it at start, and
   `GovernedTerminationActivity` shreds under it. Elsa mints a different instance id for each create.

> [!WARNING]
> Do not anchor on `WorkflowExecutionContext.Id`. The key for that id was never minted. The termination
> seems to run, but the crypto-shred does nothing and gives no error.

The variables of a native flow need `.WithWorkflowStorage()` in your Elsa registration. With this call,
the variables of your flow persist and rehydrate across a suspend and resume. Without it, a native Elsa
flow loses the state that it keeps between steps when it resumes. This applies to the variables of your
flow. The two governed variables of the framework are the sealed seed and the PII-free instance id.
They go in as workflow input.

You choose the production persistence for Elsa. The Tier-2 tests use SQLite. A real deployment configures
the Elsa EF Core provider or a different persistence provider.

```csharp
var workflow = new Workflow
{
    Root = new Sequence { Activities =
    {
        new GovStep { Step = step, Kind = "lookup", Seq = 0, Seed = seed },
        new WaitEvent { EventName = "invite-accepted" },
        new GovStep { Step = step, Kind = "assign", Seq = 1, Seed = seed },
        new GovernedTerminationActivity(),   // termination from DI, anchored on the correlation id
    } },
};
// Start with the id you sealed the seed under as the CORRELATION id (ElsaWorkflowGateway.StartAsync does
// this), so the governed steps and the termination anchor on the same id.
// GovStep.ExecuteAsync => Native.RunSealed(Step, ctx.WorkflowExecutionContext.CorrelationId!, Seq, Kind, Seed)
```

## Restate (cross-language)

Restate has no .NET SDK. You write the native flow in Rust, the language of the sidecar. The Restate
sidecar runs the sequence, the durable-promise wait, and the termination. It calls a small .NET
governed-step host over HTTP:

- `POST /gov-step` runs `step.ExecuteAsync`.
- `POST /gov-terminate` runs `termination.TerminateAsync`.

The [Restate adapter README](../../src/SoEx.Workflow.Runtime.Restate/README.md) gives the details.

## Camunda 8 / Zeebe (visual BPMN)

On Zeebe, the broker owns the flow. The parts of the flow are:

- Each governed step is a service task. A worker handles its job. The PII-free kind and the sequence go
  in static task headers.
- Each wait is a message-catch event. The message correlates on the instance id.
- The termination is a job of a process end execution listener.

1. Draw the flow as a BPMN diagram in a visual editor, Camunda Modeler or BPMN-js.
2. Deploy the `.bpmn` file.
3. Write the .NET side as in this example:

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

### Event data on Zeebe

A native flow sets which step gets the data of a raise. Keep the event that your wait received. Give its
payload to the one `ExecuteAsync` call that needs it.

On Zeebe, the gateway publishes the payload of a raise as the process variable `__event`. After you set a
Zeebe process variable, it persists in its flow scope. A worker that always forwards `__event` gives the
same old raise to each later step in the scope. Thus event data is opt-in for each service task:

1. Add an `eventVariable` task header to the task that follows the catch event.

   The header gives the name of the variable to read. Do not put the header on other tasks.

2. Use the `OpenStepWorker` overload whose delegate takes the extra `byte[]`:

```csharp
using var steps = ZeebeWorkflowHost.OpenStepWorker(client, "onboard-step", step,
    async (id, seq, kind, seed, eventData) => await Native.RunSealed(step, id, seq, kind, seed, eventData));
```

3. To clear the variable after the task, add a BPMN output mapping that overwrites it.

   This is a part of your BPMN model. The framework cannot clear the variable.

### Check the io-mappings

The framework writes two process variables: the sealed seed and the PII-free instance id. The
framework cannot control the io-mappings of your own BPMN. Thus `DeployAsync` lints each resource when it
deploys it, and returns the findings. It calls `ZeebeWorkflowHost.ValidateResource` internally. You can
also call `ValidateResource` by itself.

Each `ZeebeResourceWarning` identifies a governed task that copies `seed` or `instanceId` into a different
journaled variable. The warnings are advisory. The deployment continues when there are warnings.

1. Examine the list of warnings that `DeployAsync` returns.
2. Decide if you correct the mapping or stop the deployment.

Camunda 8 / Zeebe supports the native flow only. A BPMN graph cannot express a `WorkflowAction` loop.

## Reference

- The [runtime matrix](../reference/runtime-matrix.md) shows how each concept maps to each runtime.
- [Runtimes and durability](../explanation/runtimes-and-durability.md) describes the durability models.
  It also tells why the termination must stay off the replay path.
