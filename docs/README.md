> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# SoEx.Workflow documentation

The documentation uses the [Diátaxis](https://diataxis.fr) structure. It has four parts:

- **Tutorials** teach the product. In each tutorial, you build a workflow.
- **How-to guides** give the procedure for one task.
- **Reference** pages give the exact facts: signatures, packages, and the behavior of each runtime.
- **Explanation** pages describe the design and its trade-offs.

If you are new to SoEx.Workflow, do the tutorials first. Then use a how-to guide when you have a task.
Use the reference when you need a signature or a detail about one runtime. Use the explanations to learn
the reasons for the design.

## Tutorials

Each tutorial is a complete example that you can run from start to end.

- [**1. Build your first workflow**](tutorials/01-your-first-workflow.md). Make an onboarding flow and
  run it in-process to completion. It needs no infrastructure. Time: approximately 15 minutes.
- [**2. Erase a subject**](tutorials/02-erase-a-subject.md). Extend the first workflow. Send a request
  to forget a person, and make sure that crypto-shred made the data unrecoverable.

## How-to guides

Each guide gives the procedure for one task. The guides assume that you did the tutorials.

- [Choose a consumption model](how-to/choose-a-consumption-model.md). Choose the portable flow or a
  native flow.
- [Write a step component](how-to/write-a-step-component.md). Make the step DTOs, write the
  component, and implement the erasure events.
- [Run the portable flow](how-to/run-the-portable-flow.md). Host one component on InProc, Durable
  Task, Temporal, Elsa, or Restate.
- [Author a native flow](how-to/author-a-native-flow.md). Write the flow in the model of each runtime.
- [Drive a flow with a statechart](how-to/drive-a-flow-with-a-statechart.md). Run an XState machine
  as a step component on each runtime that the portable flow supports.
- [Evolve a running flow](how-to/evolve-a-running-flow.md). Change a flow that has live instances,
  and keep those instances in operation.
- [Trigger flows from outside](how-to/trigger-flows-from-outside.md). Start a flow and raise events on
  it from a webhook. The webhook holds only business identity.
- [Authorize the gateway seam](how-to/authorize-the-gateway-seam.md). Apply authorization at the
  trigger point, and make instance ids impossible to guess.
- [Make crypto-shred durable](how-to/make-crypto-shred-durable.md). Use a production key store,
  subject index, and idempotency store.
- [Operate in production](how-to/operate-in-production.md). Connect the metrics, set the alerts, and
  recover a held instance.
- [Run erasure maintenance](how-to/run-erasure-maintenance.md). Run the sweep, the held re-drive, and
  the deadline review. Together, they close the gaps over time.
- [Choose a message serializer](how-to/choose-a-serializer.md). Run on System.Text.Json or BoundJson,
  and declare the types that each one needs.
- [Customize PII detection](how-to/customize-pii-detection.md). Add a stricter subject matcher. PII is
  personally identifiable information.
- [Secure a PII deployment](how-to/secure-a-pii-deployment.md). Use the checklist before production.
  It lists the obligations that the threat model gives to you: keys, values in the journal in clear
  text, telemetry, transport, gateway authorization, and operations.
- [Verify it yourself](how-to/verify-it-yourself.md). Reproduce the behavior with the examples. The
  guide also lists the environment and timing traps that can give false results.

## Reference

These pages give the exact signatures, the packages, and the behavior of each runtime.

- [Packages](reference/packages.md). The contents of each NuGet package.
- [The governed core](reference/governed-core.md). `GovernedStep`, `GovernedTermination`,
  `StepContext`, and the wiring.
- [`WorkflowAction`](reference/workflow-action.md). The vocabulary of the portable flow.
- [Erasure events](reference/erasure-events.md). `IErasureEvent` and its context types.
- [Erasure API](reference/erasure-api.md). `ErasureCoordinator`, the sweep, and maintenance.
- [Governance services](reference/governance-services.md). The key store, the subject index, and the
  idempotency store.
- [Triggering](reference/triggering.md). `IWorkflowGateway`, `WorkflowSealer`,
  `DeterministicInstanceId`, and the `IWorkflowUtility` interface that a Manager calls through a proxy.
- [Runtime matrix](reference/runtime-matrix.md). How the model maps to each runtime, and where the
  semantics of the runtimes are different.
- [Transport security](reference/transport-security.md). The data that crosses each network hop, and
  how to put TLS on it. This page covers data in transit. Crypto-shred covers data at rest.
- [Glossary](reference/glossary.md). The definitions of the terms in these docs.

## Explanation

These pages give the reasons for the design.

- [The architect's view](explanation/the-architects-view.md). This page is for the IDesign Method
  architect. It shows the position of SoEx.Workflow in a system, from the Workflow utility and the
  binding. If you add SoEx.Workflow to an existing system, read this page first.
- [Consumption models](explanation/consumption-models.md). The two models, the shared governed core,
  and the reason that an instance cannot migrate between the models.
- [Crypto-shred and erasure](explanation/crypto-shred-and-erasure.md). The reason that erasure
  destroys a key, the data that is sealed and the data that is guarded, and the threat model.
- [Governance design](explanation/governance-design.md). The per-instance key, the subject index, and
  idempotency, and the reason for these three parts.
- [The triggering seam](explanation/the-triggering-seam.md). Deterministic ids, sealing without the
  endpoint, and the authorization point.
- [Runtimes and durability](explanation/runtimes-and-durability.md). The four durability models, and
  the reasons for the differences in behavior between the runtimes.
- [Versioning and evolution](explanation/versioning-and-evolution.md). What occurs on each runtime to
  in-flight instances when you change a flow and deploy it again.
