> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# How to run the portable flow

In the portable model you write one component whose step operation returns a
[`WorkflowAction`](../reference/workflow-action.md), and SoEx ships a generic per-backend driver that
owns the step loop. The same `(step, termination)` pair hosts unchanged on InProc, Durable Task,
Temporal, Elsa, and Restate. This guide shows how to host it on each.

> First [write your step component](write-a-step-component.md) (it returns a `WorkflowAction`) and
> [wire the governed core](../reference/governed-core.md) to get `step` and `termination`.

## Seal the first step

Starting a portable instance means sealing the first step into a seed, which mints the per-instance key
and encrypts the payload under it:

```csharp
byte[] seed = step.SealStep(instanceId, new OnboardStep.LookupUser("org-1", "invitee@example.com", "pro"),
    WorkflowEnvelope.AmbientFor(step.Serializer, SubjectContext.Managed("invitee@example.com")));
```

The driver seals everything else it journals too (each next-step envelope, carried continue-as-new
state, the recorded result), so the backend persists only ciphertext and you write no encryption code.
This needs a durable, shared key store in production (see
[Make crypto-shred durable](make-crypto-shred-durable.md)) and a PII-free result.

## Host on a runtime

Host the same `(step, termination)`; only the runner differs.

### InProc (in-memory, no durability)

```csharp
var runtime = new InMemoryWorkflowRuntime("inst-1");
var driver  = new WorkflowDriver<IOnboardManager>(runtime, step, termination);

Task<byte[]> completion = driver.RunAsync(seed);                       // parks on a wait
await runtime.RaiseEventAsync("inst-1", "invite-accepted",
    step.SealStep("inst-1", new OnboardStep.AssignSubscription("res-1", "confirmed-user")));
byte[] result = await completion;
```

`runtime.Advance(TimeSpan)` fires durable timers instantly for `WaitForEvent` timeouts and `Delay`.
Nothing survives a process restart; InProc is for tests and demos.

### Durable hosts

For durability, hand the same `(step, termination)` to the shipped builder for your backend:

| Runtime | Durable host builder |
|---|---|
| **Durable Task** | `DurableTaskWorkflowHost.Build(conn, step, termination)` → schedule `OrchestrationName` with `seed`; point `conn` at a Durable Task Scheduler |
| **Temporal** | `TemporalWorkflowHost.BuildWorker(client, taskQueue, step, termination)` → a `TemporalWorker` on your connected cluster client (run it with `ExecuteAsync`; a fresh worker resumes server-persisted instances) |
| **Elsa** | `ElsaWorkflowHost.BuildDurable(step, termination, configureElsa, configureServices?)` → supply your workflow(s) and a persistence provider (e.g. EF Core) in `configureElsa` |
| **Restate** | `RestateWorkflowHost.Build(stepUrl, step, termination, authToken)` → the `/step`+`/terminate` callback host; the Rust `OnboardWorkflow` in the Restate sidecar drives it (see the [Restate adapter README](../../src/SoEx.Workflow.Runtime.Restate/README.md)) |

The governed `(step, termination)` is identical across all of them; only the host builder changes.

### Test hosts

Two runtimes also ship in-memory test hosts, handy for fast, backend-free tests. Nothing survives a
restart:

| Runtime | Test host |
|---|---|
| **Temporal** | `new TemporalTestWorkflowHost(step, termination).RunAsync(id, seed, prearmedEvents)` (time-skipping) |
| **Elsa** | `new ElsaTestWorkflowHost().Start(id, step, termination, seed, prearmedEvents)` (no persistence) |

## Drive it from outside

To start or raise events on a hosted instance from a webhook that holds only business identity, see
[Trigger flows from outside](trigger-flows-from-outside.md).

## Reference

- The [`WorkflowAction` vocabulary](../reference/workflow-action.md) your component returns.
- The [packages](../reference/packages.md) each runtime needs.
- [Runtimes and durability](../explanation/runtimes-and-durability.md) — how each backend persists.
