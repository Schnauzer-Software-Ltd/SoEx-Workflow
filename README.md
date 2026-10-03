# SoEx.Workflow

This repository demonstrates decoupling business logic from workflow tooling. Validated against 
Temporal, Elsa, Durable Task, Restate, and Camunda 8. It also provides a ready to use 
IWorkflowUtility for use within IDesign Method based Architectures.

PII protection using crypto-shreading is enforced by this tooling to try and prevent 
recovery of PII from the workflow journals.

For the time being the best place to start would be running the examples and poking them.

> [!IMPORTANT]
> Below this line was LLM generated and is pending editing by the project maintainer.

# SoEx-Workflow

SoEx-Workflow runs durable workflows for SoEx subsystem entrypoints. It also erases personal data on
request. Personal data is PII (personally identifiable information).

Each workflow instance has its own encryption key. Each step of the instance runs through the SoEx host
pipeline. The framework seals the data of the instance with that key. When the instance ends, the
framework destroys the key. When a person asks to be forgotten, the framework destroys the key of each
instance that holds that person. Without the key, the sealed data is unrecoverable. This operation is
crypto-shred.

The subject index connects each subject to the instances that hold it. Erasure uses the index to find
those instances. The index keeps each subject as a one-way lookup token and a blob sealed with the key of
the instance. When the instance terminates, crypto-shred also removes its index entries.

## Limits of the protection

The [threat model](docs/explanation/crypto-shred-and-erasure.md#threat-model) gives the full details of
each limit.

- **Key-store backups.** A backup of the key store can hold a key after the framework destroys it. In
  production, keep key-store snapshots for no longer than the erasure window. Alternatively, rotate the
  master key after a shred. See
  [the key store's own backups](docs/explanation/crypto-shred-and-erasure.md#the-key-stores-own-backups).
- **Values in clear text.** Crypto-shred makes sealed data unrecoverable. The runtime journal keeps
  instance ids, step results, and final results in clear text, and these values stay after the shred.
  The framework scans these values for the subjects that it governs. This scan is a safety net for
  known subjects only. Keep PII out of these values. See
  [what is sealed vs guarded](docs/explanation/crypto-shred-and-erasure.md#what-is-sealed-vs-guarded).
- **Data in transit.** Crypto-shred applies to data at rest. On each seal, the OpenBao key store sends
  the plaintext to the server. The Restate sidecar and the Zeebe gateway use plaintext transport by
  default. Off loopback, use TLS: an `https` OpenBao address and `ZeebeWorkflowHost.ConnectSecure`. See
  the [runtime matrix](docs/reference/runtime-matrix.md).
- **Abandoned instances.** An admin terminate or purge skips the termination hook. The
  [erasure maintenance sweep](docs/how-to/run-erasure-maintenance.md) then closes the instance. You
  must schedule the sweep.
- **Erasure requests.** `RequestEraseAsync` records an erasure request and returns immediately. The
  crypto-shred occurs later, when the drain runs. The built-in maintenance runner runs the drain by
  default. Schedule the drain within your statutory deadline. Keep pending requests in a durable store.
  If you do not, the framework can lose an acknowledged request or fail to complete it. See
  [erasure maintenance](docs/how-to/run-erasure-maintenance.md).

## Runtimes

One step component runs on six runtimes:

- Durable Task
- Temporal
- Elsa
- Restate
- Camunda 8 / Zeebe
- InProc

The first five are durable production runtimes. InProc keeps state in memory only and loses it on a
restart. Use InProc for tests and demos.

```csharp
// one component, one step at a time, governed and erasable
public interface IOnboardManager
{
    Task<StepOutcome> Run(OnboardStep step);
}
```

## Consumption models

There are two consumption models. You choose one for each instance.

- **Portable flow.** You write one component. Its step operation returns a `WorkflowAction` that tells
  the driver what to do next. The SoEx driver runs the component on each runtime with no change.
- **Native flow.** You write the flow in the model of your runtime: a Temporal `[Workflow]`, a Durable
  Task orchestration, an Elsa graph, or a Camunda 8 BPMN diagram. Your component runs each step.

Both models use the same governed core. Erasure, idempotency, and the subject index work the same in
each model. Camunda 8 / Zeebe supports the native flow only. InProc supports the portable flow only. See
the [runtime matrix](docs/reference/runtime-matrix.md).
[Choose a consumption model](docs/how-to/choose-a-consumption-model.md) helps you make the decision.

## Documentation

The documentation uses the [Diátaxis](https://diataxis.fr) structure. Start at the
[documentation home](docs/README.md), or go to the part that you need:

- [Build your first workflow](docs/tutorials/01-your-first-workflow.md) is the first tutorial. It runs
  in-process and needs no infrastructure.
- The [how-to guides](docs/README.md#how-to-guides) give the procedure for each task. Examples: write a
  component, host it on a runtime, trigger it from a webhook, make crypto-shred durable.
- The [reference](docs/README.md#reference) describes each type, each package, and the behavior of each
  runtime.
- The [explanations](docs/README.md#explanation) describe the design: crypto-shred, the two consumption
  models, and erasure.

## Provenance and licensing

The license is the [MIT License](LICENSE), © 2026 [Schnauzer Software Ltd](https://schnauzer.software/).
An AI generated the code. See [AI-PROVENANCE.md](AI-PROVENANCE.md). Do not upstream this code into the
SoEx core, which humans write. To contribute, see [CONTRIBUTING.md](CONTRIBUTING.md).
[THIRD-PARTY-LICENSES.md](THIRD-PARTY-LICENSES.md) gives the third-party attribution for the full
transitive closure. All of these licenses are permissive. The target framework is `net10.0`.

The test suite is in a separate private repository. To buy access to it, contact
[support@schnauzersoftware.co.uk](mailto:support@schnauzersoftware.co.uk).
