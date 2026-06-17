> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Packages

These packages target `net10.0`. They are not yet published on nuget.org, so consume them by project
reference (or build from source and reference the built assemblies); the package ids below are the
assembly/project names. Most adapters ship both consumption models (the native `Governed*` hook(s) and
the portable flow) in one package, because the choice is made per instance rather than per package.
Camunda 8 / Zeebe is native-only.

## Core and adapters

| Package | Native flow | Portable flow |
|---|---|---|
| `SoEx.Workflow` | `GovernedStep<I>`, `GovernedTermination` + the governed-core plumbing (sealer, gateway/runtime/dispatch seams, erasure coordinators) over the abstractions packages | `WorkflowDriver<I>` — the portable driver |
| `SoEx.Workflow.Runtime.InMemory` | the in-memory governance stores (key / index / idempotency + maintenance) and `InProcWorkflowGateway` | `InMemoryWorkflowRuntime` — the in-process driver target |
| `SoEx.Workflow.Runtime.DurableTask` | `GovernedTaskOrchestrator<TIn,TOut>` + `GovernedTerminationActivity` | `WorkflowOrchestration` + `StepActivity`/`TerminateActivity` + `DurableTaskWorkflowHost` |
| `SoEx.Workflow.Runtime.Temporal` | `GovernedTerminationInterceptor` + `GovernedTerminationActivities` | `WorkflowOrchestration` + `WorkflowActivities` + `TemporalWorkflowHost.BuildWorker` (durable) / `TemporalTestWorkflowHost` (time-skipping) |
| `SoEx.Workflow.Runtime.Elsa` | `GovernedTerminationActivity` | `WorkflowDriverActivity` + `ElsaWorkflowHost.BuildDurable` (durable) / `ElsaTestWorkflowHost` (in-memory) |
| `SoEx.Workflow.Runtime.Restate` | the native Rust `NativeOnboardWorkflow` sidecar handler | `RestateWorkflowHost` + the Rust `OnboardWorkflow` sidecar handler — see the [adapter README](../../src/SoEx.Workflow.Runtime.Restate/README.md) |
| `SoEx.Workflow.Runtime.Zeebe` | `ZeebeWorkflowHost` (`OpenStepWorker` + `OpenTerminationListener`) over a BPMN graph | — *(native-only)* |

## Abstractions

A consumer references these rather than the governed core: a business component compiles against the
authoring package; a durable backend implements the persistence package. Both keep their types in the
`SoEx.Workflow` namespace, so they are a narrower assembly reference, not a different `using`.

| Package | Ships |
|---|---|
| `SoEx.Workflow.Abstractions` | the authoring contracts a component compiles against: `WorkflowAction` (+ extensions), `IErasureEvents` + its contexts, `DeterministicInstanceId`, `IdempotencyKey` |
| `SoEx.Workflow.Stores.Abstractions` | the persistence contracts a durable backend implements: `IInstanceKeyStore` / `ISubjectIndex` / `IIdempotencyStore`, the erasure-maintenance registries, and `ISubjectProtector` (+ `HmacSubjectProtector`). References `SoEx.Workflow.Abstractions` |

## Consumer-side utility (IDesign Method component)

| Package | Ships |
|---|---|
| `SoEx.Method.Workflow` | `WorkflowUtility` — the reusable durable-workflow plumbing a peer entry component proxies to (start/raise-event/recover) and the host calls as a system client (erase/sweep), over the `WorkflowSeam` |
| `SoEx.Method.Workflow.Abstractions` | the proxied `IWorkflowUtility` faces (the `SubSystem` and `External` contracts) |

## Durable governance stores

Swap these in for the in-memory defaults (`SoEx.Workflow.Runtime.InMemory`) at your composition root — see
[Make crypto-shred durable](../how-to/make-crypto-shred-durable.md). Each implements a contract from
`SoEx.Workflow.Stores.Abstractions`.

| Package | Ships |
|---|---|
| `SoEx.Workflow.Keys.OpenBao` | `OpenBaoInstanceKeyStore` (OpenBao Transit; key never leaves the server) |
| `SoEx.Workflow.Keys.RavenDB` | `RavenDbInstanceKeyStore` (master-key-wrapped data key in compare-exchange) |
| `SoEx.Workflow.SubjectIndex.RavenDB` | `RavenDbSubjectIndex` |
| `SoEx.Workflow.SubjectIndex.EfCore` | `EfCoreSubjectIndex` (provider-agnostic) |
| `SoEx.Workflow.Idempotency.RavenDB` | `RavenDbIdempotencyStore` (compare-exchange) |
| `SoEx.Workflow.Maintenance.RavenDB` | RavenDB `IHeldInstanceRegistry` + `IErasureRequestRegistry`, plus `RavenErasureStores` (one document store backs these and the subject index) |
| `SoEx.Workflow.Maintenance.EfCore` | EF Core `IHeldInstanceRegistry` + `IErasureRequestRegistry`, plus `ErasureStores` (one database backs these and the subject index) |

The two `Maintenance` packages reference their matching `SubjectIndex` package, so the bundle can back the
subject index from the same store; see
[Back the index and maintenance from one store](../how-to/make-crypto-shred-durable.md#optional-back-the-index-and-maintenance-from-one-store).

## SoEx dependencies at the composition root

The core `SoEx.Workflow` package is a plain binding/transport and takes no `SoEx.Hosting` dependency.
Reference these at process startup:

- `SoEx.Hosting` — to stand up the host. It bundles the default serializer
  (`SoEx.Hosting.Serializers.NewtonsoftJson.JsonMessageSerializer`), which the host registers as the
  `IMessageSerializer` automatically, so there's no separate serializer package to add.
- `SoEx.Context` — if a step reads the ambient `SubjectContext`.
