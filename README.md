> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# SoEx-Workflow

Governed, durable workflow execution for SoEx subsystem entrypoints, with built-in right-to-erasure.
Every step runs through the SoEx host pipeline under a per-instance encryption key. When an instance
ends, or when someone asks you to forget a subject, that key is destroyed and everything the framework
sealed under it becomes unrecoverable. This is crypto-shred. The durable subject-to-instance index that
erasure routing depends on gets the same treatment: it stores each subject only as a one-way lookup
token plus a blob sealed under that instance's key, so it holds no recoverable subject at rest and is
shredded along with the instance when it terminates.

A few caveats to know up front; the [threat model](docs/explanation/crypto-shred-and-erasure.md#threat-model)
covers each in full:

- **The key store is the remaining concern.** A key-store backup taken before the destroy can still hold
  the key, so in production you bound key-store snapshot retention to the erasure window (or rotate the
  master key after shreds). See
  [the key store's own backups](docs/explanation/crypto-shred-and-erasure.md#the-key-stores-own-backups).
- **Crypto-shred protects what was sealed, not what the engine reads in clear.** Instance ids and step or
  final results are journaled in clear and survive the shred; the framework guards them with a substring
  scan for subjects it already governs, which is a safety net, not a universal PII scanner. Keep these
  PII-free by construction — see
  [what is sealed vs guarded](docs/explanation/crypto-shred-and-erasure.md#what-is-sealed-vs-guarded).
- **It is data-at-rest erasure, not confidentiality in flight.** Payloads are sealed at rest, but the
  OpenBao key store sends the plaintext to the server on every seal, and the Restate sidecar and Zeebe
  gateway default to plaintext transport — use TLS (an `https` OpenBao address, `ZeebeWorkflowHost.ConnectSecure`)
  off loopback. See the [runtime matrix](docs/reference/runtime-matrix.md).
- **An abandoned instance is shredded by the sweep, not instantly.** An admin terminate/purge bypasses the
  termination hook, so closure then depends on the
  [erasure maintenance sweep](docs/how-to/run-erasure-maintenance.md) being scheduled.
- **An erasure request is acknowledged, then shredded on a drain pass.** Right-to-erasure is admit-and-drain:
  `RequestEraseAsync` records a request and returns at once; the crypto-shred happens when the drain runs
  (the built-in maintenance runner does it by default). Schedule the drain within your statutory deadline and
  back it with a durable pending store, or an acknowledged request can be lost or never honoured. See
  [erasure maintenance](docs/how-to/run-erasure-maintenance.md).

The same component runs on six runtimes: five durable production engines — Durable Task, Temporal, Elsa,
Restate, and Camunda 8 / Zeebe — plus in-process, which is for tests and demos and keeps no state across
a restart.

```csharp
// one component, one step at a time, governed and erasable
public interface IOnboardSteps
{
    Task<StepOutcome> Run(OnboardStep step);
}
```

## Two ways to consume it

You pick one per instance:

- **Portable flow.** Write one component that returns a `WorkflowAction` describing what to do next.
  SoEx's generic driver runs it unchanged on every runtime.
- **Native flow.** Author the flow in your runtime's own model (a Temporal `[Workflow]`, a Durable Task
  orchestration, an Elsa graph, a Camunda 8 BPMN diagram), and your component just runs each step.

Both are built on the same governed core, so erasure, idempotency, and the subject index behave the same
either way. There are two runtime exceptions: Camunda 8 / Zeebe is native-only, and in-process is
portable-only (see the [runtime matrix](docs/reference/runtime-matrix.md)).
[Choose a consumption model](docs/how-to/choose-a-consumption-model.md) walks through the decision.

## Documentation

The docs follow the [Diátaxis](https://diataxis.fr) structure. Start at the
[documentation home](docs/README.md), or go straight to what you need:

- If you're new, start with [Build your first workflow](docs/tutorials/01-your-first-workflow.md), a
  runnable in-process example that needs no infrastructure.
- The [how-to guides](docs/README.md#how-to-guides) cover specific tasks: writing a component, hosting
  it on a runtime, triggering it from a webhook, making crypto-shred durable.
- The [reference](docs/README.md#reference) documents every type, package, and per-runtime behavior.
- The [explanations](docs/README.md#explanation) cover the design: why crypto-shred, why two
  consumption models, how erasure works.

## Provenance and licensing

Licensed under the [MIT License](LICENSE), © 2026 [Schnauzer Software Ltd](https://schnauzer.software/).
The code is AI-generated (see [AI-PROVENANCE.md](AI-PROVENANCE.md)) and must not be upstreamed into the
human-authored SoEx core. To contribute, see [CONTRIBUTING.md](CONTRIBUTING.md). Third-party attribution
(the full transitive closure, all of it permissive) is in
[THIRD-PARTY-LICENSES.md](THIRD-PARTY-LICENSES.md). Targets `net10.0`.

The test suite lives in a separate private repository. If you'd like to purchase access to it, contact
[support@schnauzersoftware.co.uk](mailto:support@schnauzersoftware.co.uk).
