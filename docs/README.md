> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# SoEx.Workflow documentation

The documentation follows [Diátaxis](https://diataxis.fr), so it is split into four kinds of material:
tutorials that teach by building, how-to guides for specific tasks, reference pages for exact facts,
and explanation pages for the design and its trade-offs. If you're new, do the tutorials first. After
that, reach for a how-to guide when you have a task in front of you, the reference when you need a
signature or a per-runtime detail, and the explanations when you want to understand why things work the
way they do.

## Tutorials

Complete, runnable examples you can follow end to end.

- [**1. Build your first workflow**](tutorials/01-your-first-workflow.md) — model an onboarding flow,
  run it in-process with no infrastructure, and watch it complete. About 15 minutes.
- [**2. Erase a subject**](tutorials/02-erase-a-subject.md) — extend that workflow, then issue a
  "forget this person" request and verify that crypto-shred made the data unrecoverable.

## How-to guides

Task-oriented recipes. Each answers a single "how do I…?" and assumes you've done the tutorials.

- [Choose a consumption model](how-to/choose-a-consumption-model.md) — portable flow vs native flow.
- [Write a step component](how-to/write-a-step-component.md) — model steps, write the component,
  implement the erasure events.
- [Run the portable flow](how-to/run-the-portable-flow.md) — host one component on InProc, Durable
  Task, Temporal, Elsa, or Restate.
- [Author a native flow](how-to/author-a-native-flow.md) — author the flow in each runtime's own model.
- [Trigger flows from outside](how-to/trigger-flows-from-outside.md) — start and raise events on a flow
  from a webhook that holds only business identity.
- [Authorize the gateway seam](how-to/authorize-the-gateway-seam.md) — enforce auth at the trigger
  chokepoint and make instance ids unguessable.
- [Make crypto-shred durable](how-to/make-crypto-shred-durable.md) — swap in a production key store,
  subject index, and idempotency store.
- [Run erasure maintenance](how-to/run-erasure-maintenance.md) — the sweep, held re-drive, and deadline
  review that close the gaps over time.
- [Customize PII detection](how-to/customize-pii-detection.md) — plug in a stricter subject matcher.
- [Secure a PII deployment](how-to/secure-a-pii-deployment.md) — the pre-production checklist of obligations
  the threat model leaves to you: keys, clear-journaled values, telemetry, transport, gateway auth, ops.
- [Verify it yourself](how-to/verify-it-yourself.md) — reproduce the behaviour with the examples, and
  the environment and timing traps that otherwise produce false results.

## Reference

Exact signatures, packages, and per-runtime behavior.

- [Packages](reference/packages.md) — what each NuGet package ships.
- [The governed core](reference/governed-core.md) — `GovernedStep`, `GovernedTermination`, `StepContext`,
  wiring.
- [`WorkflowAction`](reference/workflow-action.md) — the portable-model vocabulary.
- [Erasure events](reference/erasure-events.md) — `IErasureEvents` and its context types.
- [Erasure API](reference/erasure-api.md) — `ErasureCoordinator`, the sweep, and maintenance.
- [Governance services](reference/governance-services.md) — key store, subject index, idempotency store.
- [Triggering](reference/triggering.md) — `IWorkflowGateway`, `WorkflowSealer`, `DeterministicInstanceId`.
- [Runtime matrix](reference/runtime-matrix.md) — how the model maps to each runtime, and where their
  semantics diverge.
- [Transport security](reference/transport-security.md) — the in-flight companion to crypto-shred: what
  crosses each network hop, and how to put TLS on it.
- [Glossary](reference/glossary.md) — the terms these docs use, defined.

## Explanation

The reasoning behind the design.

- [The architect's view](explanation/the-architects-view.md) — for the IDesign Method architect: where
  SoEx.Workflow fits, starting at the Workflow utility and the binding. Read this first if you're
  integrating it into an existing system.
- [Consumption models](explanation/consumption-models.md) — the two models, the shared governed core,
  and why there's no migration between them.
- [Crypto-shred and erasure](explanation/crypto-shred-and-erasure.md) — why erasure is key destruction,
  what is sealed vs guarded, and the threat model.
- [Governance design](explanation/governance-design.md) — the per-instance key, subject index, and
  idempotency, and why those three.
- [The triggering seam](explanation/the-triggering-seam.md) — deterministic ids, sealing without the
  endpoint, and the authorization chokepoint.
- [Runtimes and durability](explanation/runtimes-and-durability.md) — the four durability models and
  why the runtimes don't all behave the same.
