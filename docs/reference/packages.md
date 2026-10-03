> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Packages

The SoEx.Workflow packages target `net10.0`. They are not published on nuget.org yet. Consume them by
project reference, or build from source and reference the built assemblies. The package ids below are
the assembly and project names.

You choose the consumption model for each instance. For this reason, most adapters contain both
consumption models in one package: the native `Governed*` hooks and the portable flow. Camunda 8 / Zeebe
supports the native flow only.

## Core and adapters

| Package | Native flow | Portable flow |
|---|---|---|
| `SoEx.Workflow` | `GovernedStep<I>`, `GovernedTermination`, and the governed-core plumbing (sealer, gateway/runtime/dispatch seams, erasure coordinators) over the abstractions packages | `WorkflowDriver<I>`, the portable driver |
| `SoEx.Workflow.Runtime.InMemory` | the in-memory governance stores (key, index, idempotency, and maintenance) and `InProcWorkflowGateway` | `InMemoryWorkflowRuntime`, the in-process driver target |
| `SoEx.Workflow.Runtime.DurableTask` | `GovernedTaskOrchestrator<TIn,TOut>` and `GovernedTerminationActivity` | `WorkflowOrchestration`, `StepActivity`/`TerminateActivity`, and `DurableTaskWorkflowHost` |
| `SoEx.Workflow.Runtime.Temporal` | `GovernedTerminationInterceptor` and `GovernedTerminationActivities` | `WorkflowOrchestration`, `WorkflowActivities`, and `TemporalWorkflowHost.BuildWorker` (durable) / `TemporalTestWorkflowHost` (time-skipping) |
| `SoEx.Workflow.Runtime.Elsa` | `GovernedTerminationActivity` | `WorkflowDriverActivity` and `ElsaWorkflowHost.BuildDurable` (durable) / `ElsaTestWorkflowHost` (in-memory) |
| `SoEx.Workflow.Runtime.Restate` | one fixed native onboarding flow (`NativeOnboardWorkflow`) in the Rust sidecar. You write your own native flow as a Rust Restate service in your own sidecar, with your own `/gov-step` and `/gov-terminate` host. | `RestateWorkflowHost` and the Rust `OnboardWorkflow` sidecar handler. See the [adapter README](../../src/SoEx.Workflow.Runtime.Restate/README.md). |
| `SoEx.Workflow.Runtime.Zeebe` | `ZeebeWorkflowHost` (`OpenStepWorker` and `OpenTerminationListener`) over a BPMN graph | — *(native flow only)* |

## Binding and statecharts

| Package | Contents |
|---|---|
| `SoEx.Transport.Workflow` | the SoEx binding and transport that host a step component: `WorkflowBinding<I>`, `WorkflowTransport`, `WorkflowEndpoint<I>`, `WorkflowActivityChannel<I>`, and `WorkflowListeners`. It references `SoEx.Workflow`. |
| `SoEx.Workflow.Statecharts` | runs a statechart as a step component: `StatechartStep`, `StatechartRouter`, `MachineStep`, `MachineEventData`, `StatechartOptions`, and `ScxmlDurability`. It references `SoEx.Workflow` and the `XState` package. See [Drive a flow with a statechart](../how-to/drive-a-flow-with-a-statechart.md). |

## Abstractions

A consumer references these packages, and does not reference the governed core. A business component
compiles against the authoring package. A durable store implements the contracts of the persistence
package. Both packages keep their types in the `SoEx.Workflow` namespace. Thus they give a narrower
assembly reference with the same `using`.

| Package | Contents |
|---|---|
| `SoEx.Workflow.Abstractions` | the authoring contracts that a component compiles against: `WorkflowAction` and its extensions, `IErasureEvent` and its contexts, `DeterministicInstanceId`, `IdempotencyKey` |
| `SoEx.Workflow.Stores.Abstractions` | the persistence contracts that a durable store implements: `IInstanceKeyStore` / `ISubjectIndex` / `IIdempotencyStore`, the erasure-maintenance registries, and `ISubjectProtector` (and `HmacSubjectProtector`). It references `SoEx.Workflow.Abstractions`. |

## Consumer-side utility (IDesign Method component)

| Package | Contents |
|---|---|
| `SoEx.Method.Workflow` | `WorkflowUtility`, the reusable durable-workflow plumbing over the `WorkflowSeam`. A peer entry component proxies to it for start, raise-event, and recover. The host calls it as a system client for erase and sweep. |
| `SoEx.Method.Workflow.Abstractions` | the proxied `IWorkflowUtility` faces (the `SubSystem` and `External` contracts) |

## Durable governance stores

These packages replace the in-memory defaults of `SoEx.Workflow.Runtime.InMemory`. Configure them at your
composition root. See [Make crypto-shred durable](../how-to/make-crypto-shred-durable.md). Each package
implements a contract from `SoEx.Workflow.Stores.Abstractions`.

| Package | Contents |
|---|---|
| `SoEx.Workflow.Keys.OpenBao` | `OpenBaoInstanceKeyStore` (OpenBao Transit. The key stays on the server.) |
| `SoEx.Workflow.Keys.RavenDB` | `RavenDbInstanceKeyStore` (a data key wrapped with the master key, in compare-exchange) |
| `SoEx.Workflow.SubjectIndex.RavenDB` | `RavenDbSubjectIndex` |
| `SoEx.Workflow.SubjectIndex.EfCore` | `EfCoreSubjectIndex` (works with each provider) |
| `SoEx.Workflow.Idempotency.RavenDB` | `RavenDbIdempotencyStore` (compare-exchange) |
| `SoEx.Workflow.Maintenance.RavenDB` | RavenDB `IHeldInstanceRegistry` and `IErasureRequestRegistry`, and `RavenErasureStores`. One document store holds these registries and the subject index. |
| `SoEx.Workflow.Maintenance.EfCore` | EF Core `IHeldInstanceRegistry` and `IErasureRequestRegistry`, and `ErasureStores`. One database holds these registries and the subject index. |

Each `Maintenance` package references its matching `SubjectIndex` package. Thus one store can hold the
maintenance registries and the subject index. See
[Back the index and maintenance from one store](../how-to/make-crypto-shred-durable.md#optional-back-the-index-and-maintenance-from-one-store).

## SoEx dependencies at the composition root

The core `SoEx.Workflow` package is a binding and transport package. It has no dependency on
`SoEx.Hosting`. Reference these packages at process startup:

- `SoEx.Hosting` starts the host. It contains the default serializer
  (`SoEx.Hosting.Serializers.SystemText.JsonMessageSerializer`). The host registers this serializer as
  the `IMessageSerializer` automatically, so you add no separate serializer package. The serializer needs
  the [known types](../how-to/choose-a-serializer.md#declare-the-known-types) that you pass to
  `builder.SoEx`.
- `SoEx.Context` is necessary if a step reads the ambient `SubjectContext`.

## Shipping status and upgrade paths

Read these limits before you build a deployment on SoEx.Workflow. They are disclosed and open.

- **No published packages, no release versions, no CI.** These items are deferred. The projects can be
  packed, but the packages are not published on nuget.org. The repository has no git tags, and the
  projects set no release version. The repository has no CI pipeline. Consume the code by project
  reference at a pinned commit. Run the attestation again locally before you deploy. Packaging and a CI publish leg
  are planned future work.
- **The substrate is a pinned prerelease.** The base SoEx packages are pinned to one exact prerelease,
  `0.0.0-alpha-4.1`. There is no compatibility policy. Until a stable line exists, treat each substrate
  update as a breaking change. Rebuild and run the attestation again against it.
- **The governance stores create their schema with `EnsureCreated()`.** The EF Core subject index and
  maintenance stores build their schema on first use. They have no migrations. Thus there is no defined
  in-place upgrade when a schema changes. Today, a schema change needs a new database.

  > [!WARNING]
  > Own the schema migrations if you run the EF-backed stores in production. Generate EF migrations
  > against the `DbContext`s until first-class migrations are available. A schema change otherwise needs
  > a new database.
- **Durable Task uses a preview SDK, verified on the emulator only.** The Durable Task adapter pins
  `Microsoft.DurableTask.* 1.25.0-preview.1`. The tests run it against the DTS **emulator**. They do not
  run it against a real Azure Durable Task Scheduler. Here, "Durable Task works" means "the emulator
  works". Validate against real DTS before you rely on it. Expect changes to the preview SDK.
- **The Restate sidecar has a versioned wire contract, and you build it by hand.** The .NET host and the
  Rust sidecar exchange a wire-contract version on each `/step` and `/terminate` call
  (`RestateWorkflowHost.WireVersion`). The host **refuses a mismatch**. Thus a stale sidecar binary
  cannot silently run an old contract. The `/step` and `/terminate` callbacks have a request timeout
  (`STEP_TIMEOUT_SECS`, default 60). A host that stops responding thus cannot block the invocation
  indefinitely. You build the sidecar in the source tree with `cargo build --release`. It is not
  available as a versioned container image. The sidecar must run under a **supervisor**, for example a
  `systemd` or container restart policy. Rebuild it together with the host each time `WireVersion`
  changes. See the [adapter README](../../src/SoEx.Workflow.Runtime.Restate/README.md).
