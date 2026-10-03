> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# How to run the portable flow

In the portable flow, you write one component. Its step operation returns a
[`WorkflowAction`](../reference/workflow-action.md). SoEx supplies a driver for each runtime, and the
driver runs the step loop. One `(step, termination)` pair runs with no change on InProc, Durable Task,
Temporal, Elsa, and Restate. This guide shows how to host the pair on each runtime.

Before you start, do these two tasks:

1. [Write your step component](write-a-step-component.md). Its step operation must return a
   `WorkflowAction`.
2. [Wire the governed core](../reference/governed-core.md). The result is `step` and `termination`.

## Seal the first step

1. Seal the first step into a seed with `step.SealStep`.

   This operation starts a portable instance. It makes the per-instance key and encrypts the payload
   with that key.

```csharp
byte[] seed = step.SealStep(instanceId, new OnboardStep.Lookup("invitee@example.com"),
    WorkflowEnvelope.AmbientFor(step.Serializer, SubjectContext.Managed("invitee@example.com")));
```

The driver also seals all other data that it writes to the journal:

- each next-step envelope
- the state that a continue-as-new carries
- the recorded result

The runtime keeps only ciphertext, and you write no encryption code. In production, this needs two
things:

- a durable, shared key store. See [Make crypto-shred durable](make-crypto-shred-durable.md).
- a result that contains no PII (personally identifiable information).

## Host on a runtime

Each runtime uses the same `(step, termination)` pair. Only the runner is different.

### InProc (in-memory, no durability)

1. Make an `InMemoryWorkflowRuntime` and a `WorkflowDriver` for your component.
2. Start the driver with `driver.RunAsync(seed)`.

   The run stops at a wait and holds there.
3. Raise the event with `runtime.RaiseEventAsync`.
4. Await the completion to get the result.

```csharp
var runtime = new InMemoryWorkflowRuntime("inst-1");
var driver  = new WorkflowDriver<IOnboardManager>(runtime, step, termination);

Task<byte[]> completion = driver.RunAsync(seed);                       // parks on a wait
await runtime.RaiseEventAsync("inst-1", "invite-accepted",
    step.SealStep("inst-1", new OnboardStep.Assign("res-1", "confirmed-user")));
byte[] result = await completion;
```

`runtime.Advance(TimeSpan)` fires durable timers immediately for `WaitForEvent` timeouts and `Delay`.
InProc keeps state in memory only, and it loses all state when the process restarts. Use InProc for
tests and demos.

### Durable hosts

1. Find the durable host builder for your runtime in the table.
2. Give the same `(step, termination)` pair to that builder.

| Runtime | Durable host builder |
|---|---|
| **Durable Task** | `DurableTaskWorkflowHost.Build(conn, step, termination)` → schedule `OrchestrationName` with `seed`. Set `conn` to a Durable Task Scheduler. |
| **Temporal** | `TemporalWorkflowHost.BuildWorker(client, taskQueue, step, termination)` → a `TemporalWorker` on your connected cluster client. Run it with `ExecuteAsync`. A new worker resumes the instances that the server keeps. |
| **Elsa** | `ElsaWorkflowHost.BuildDurable(step, termination, configureElsa, configureServices?)` → in `configureElsa`, supply your workflow(s) and a persistence provider (for example, EF Core). |
| **Restate** | `RestateWorkflowHost.Build(stepUrl, step, termination, authToken)` → the `/step`+`/terminate` callback host. The Rust `OnboardWorkflow` in the Restate sidecar drives it. See the [Restate adapter README](../../src/SoEx.Workflow.Runtime.Restate/README.md). |

The governed `(step, termination)` pair is the same on all these runtimes. Only the host builder
changes.

#### Elsa

1. On Elsa, register a workflow definition with one activity. Make the shipped driver its root.

```csharp
public class OnboardWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder) => builder.Root = new WorkflowDriverActivity();
}
```

2. Do not set properties on the driver.

   Elsa builds a registered definition one time, and that build has no access to the run input. An
   instance that Elsa loads again on a new host holds no live object references. Thus the driver gets
   its data from these sources:

   - The governed core comes from DI. `BuildDurable` registers both parts.
   - The saga id comes from the Elsa correlation id.
   - The sealed seed comes from the `seed` workflow input.

3. Start instances and raise events through [`ElsaWorkflowGateway`](trigger-flows-from-outside.md).

   The gateway sets the correlation id and the `seed` input. Elsa publishes the completed result to the
   `soex:result` workflow variable.

You can set `Step`/`Termination`/`SagaInstanceId`/`Seed` on the activity when you build the definition
for each run and hold it yourself. `ElsaTestWorkflowHost` uses this form in memory. This form loses its
state on a restart, so it is not a durable form.

### Test hosts

Temporal and Elsa also supply in-memory test hosts. Use them for fast tests that need no runtime
service. A test host loses all state on a restart.

| Runtime | Test host |
|---|---|
| **Temporal** | `new TemporalTestWorkflowHost(step, termination).RunAsync(id, seed, prearmedEvents)` (time-skipping) |
| **Elsa** | `new ElsaTestWorkflowHost().Start(id, step, termination, seed, prearmedEvents)` (no persistence) |

## Drive it from outside

A webhook that holds only business identity can start a hosted instance and raise events on it. See
[Trigger flows from outside](trigger-flows-from-outside.md).

## Reference

- The [`WorkflowAction` vocabulary](../reference/workflow-action.md) that your component returns.
- The [packages](../reference/packages.md) that each runtime needs.
- [Runtimes and durability](../explanation/runtimes-and-durability.md). How each runtime keeps its
  data.
