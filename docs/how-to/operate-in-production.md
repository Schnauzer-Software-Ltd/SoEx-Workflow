# Operate in production

What to watch, what to alert on, and how to recover once SoEx-Workflow is carrying real traffic. This is the
day-2 companion to [Secure a PII deployment](secure-a-pii-deployment.md) (the pre-production checklist) and
[Run erasure maintenance](run-erasure-maintenance.md) (the maintenance runner itself).

## Wire the metrics

The framework emits a `System.Diagnostics.Metrics` meter named `SoEx.Workflow`. Subscribe to it from your
telemetry stack, for example with OpenTelemetry:

```csharp
builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter(WorkflowMetrics.MeterName));
```

Then create one `WorkflowMetrics` at the composition root and thread it into the governed components that emit
(the governed step, the termination, the erasure coordinator all take an optional `WorkflowMetrics`), and call
`TrackBacklog(pendingStore)` once so the backlog gauges read your live pending-erasure store.

The instruments (names are a stable contract you can build dashboards against):

| Instrument | Kind | Meaning |
|---|---|---|
| `soex.workflow.steps.executed` | counter | Governed steps dispatched successfully (per attempt). |
| `soex.workflow.steps.failed` | counter | Governed step dispatches that threw (per attempt, so this includes retries). |
| `soex.workflow.shreds` | counter, tag `outcome=complete\|held` | Terminations that crypto-shredded, vs those parked (key retained). |
| `soex.workflow.sweep.instances` | counter, tag `state=complete\|held\|unresolved` | Instances handled by an abandoned-instance sweep pass. |
| `soex.workflow.erasure.deadline_escalations` | counter | Erasure requests whose statutory deadline was escalated. |
| `soex.workflow.erasure.backlog.count` | gauge | Open erasure requests awaiting a drain. |
| `soex.workflow.erasure.backlog.oldest_age.seconds` | gauge | Age of the oldest un-drained erasure request. |

## What to alert on

- **Backlog age.** `erasure.backlog.oldest_age.seconds` climbing past a fraction of your statutory deadline
  means the maintenance runner is not keeping up (or has stopped). This is the single most important alert: it
  is the one that fires before a deadline is breached.
- **Held count.** A rising `shreds{outcome=held}` or `sweep.instances{state=held}` means instances are parking
  instead of completing their erasure. Each held instance keeps its key and needs an audited re-drive.
- **Unresolved sweeps.** `sweep.instances{state=unresolved}` means the sweep found aged instances your resolver
  could not map to a target. Their keys survive until you can resolve them, so investigate rather than ignore.
- **Step failure rate.** A sustained `steps.failed` rate signals a poison step retrying, or a dependency down.

## Do not let the maintenance loops go silent

The erasure sweep and the maintenance loop keep running across a transient failure, but they will not tell you
they failed unless you wire the hook. Both loops take an `onError` callback and hand it the running
`LoopPassHealth` (consecutive failures, last success timestamp), and an `onPass` callback with each pass report.
Wire `onError` to your logging and alerting, and alert on `LoopPassHealth.ConsecutiveFailures` or a stale
`LastSuccess`. A backstop that has silently stopped while credentials expired is the failure mode this exists
to catch. Run the maintenance runner on exactly one instance and monitor it from outside.

## Recover a held instance

A held instance is not lost: its key is retained and it is recorded in the held registry. Enumerate the
registry, investigate the recorded (subject-free) failure reason, then re-drive it through the termination
coordinator (`ReDriveAsync`), which re-runs the retention extraction and, on success, completes the shred. A
crash between `Destroy` and the index prune is repaired the same way: a re-entered termination sees the key
already gone and re-prunes the dangling edge.

## Validate configuration at start

Do not ship dev defaults. Supply every endpoint and secret explicitly and fail fast when one is missing rather
than falling back to a built-in default (the example host now refuses to default the OpenBao token to `root`).
Give the OpenBao token rights only on the Transit mount; keep a RavenDB master KEK in a KMS/HSM, not app config.

## Known operational gaps

These are disclosed rather than closed; account for them in your runbook.

- **No packages or CI.** Consume by project reference at a pinned commit and re-run the attestation locally.
  There is no NuGet package, no versioning, and no CI gate. See [packages](../reference/packages.md).
- **Restate step retry is unbounded.** On the Restate sidecar a failing step retries with infinite backoff and
  does not yet park. Wire external stuck-instance alerting on the Restate leg. See the failure row in the
  [runtime matrix](../reference/runtime-matrix.md#step-failure-retry-and-poison).
- **OpenBao shred finality is bounded by snapshot retention.** With no client-held KEK to rotate, a restored
  pre-destroy storage snapshot reverses a shred. Bound snapshot retention below your erasure deadline and guard
  unseal-key custody. See [Make crypto-shred durable](make-crypto-shred-durable.md).
