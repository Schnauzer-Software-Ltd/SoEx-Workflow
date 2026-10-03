> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Operate in production

This guide tells you what to monitor, what to alert on, and how to recover when SoEx-Workflow has real
traffic. Use it after you go live. Use it with two other guides:

- [Secure a PII deployment](secure-a-pii-deployment.md): the checklist before production.
- [Run erasure maintenance](run-erasure-maintenance.md): the maintenance runner.

## Wire the metrics

The framework emits a `System.Diagnostics.Metrics` meter with the name `SoEx.Workflow`.

1. Subscribe to the meter from your telemetry stack. This example uses OpenTelemetry.

```csharp
builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter(WorkflowMetrics.MeterName));
```

2. Create one `WorkflowMetrics` at the composition root.
3. Pass it to each governed component that emits metrics. The governed step, the termination, and the
   erasure coordinator each take an optional `WorkflowMetrics`.
4. Call `TrackBacklog(pendingStore)` one time. The backlog gauges then read your live pending-erasure
   store.

The names of the instruments are a stable contract. You can build dashboards on them.

| Instrument | Kind | Meaning |
|---|---|---|
| `soex.workflow.steps.executed` | counter | Governed steps that dispatched successfully. It counts each attempt. |
| `soex.workflow.steps.failed` | counter | Governed step dispatches that threw. It counts each attempt, so it includes retries. |
| `soex.workflow.shreds` | counter, tag `outcome=complete\|held` | Terminations that crypto-shredded (`complete`), and terminations that went to held state with the key kept (`held`). |
| `soex.workflow.sweep.instances` | counter, tag `state=complete\|held\|unresolved` | Instances that a pass of the abandoned-instance sweep handled. |
| `soex.workflow.erasure.deadline_escalations` | counter | Erasure requests with an escalated statutory deadline. |
| `soex.workflow.erasure.backlog.count` | gauge | Open erasure requests that wait for a drain. |
| `soex.workflow.erasure.backlog.oldest_age.seconds` | gauge | The age of the oldest erasure request that the drain has not processed. |

## What to alert on

- **Backlog age.** This is the most important alert. It fires before a deadline is breached. If
  `erasure.backlog.oldest_age.seconds` increases past a fraction of your statutory deadline, the
  maintenance runner is too slow or has stopped.
- **Held count.** If `shreds{outcome=held}` or `sweep.instances{state=held}` increases, instances go to
  held state and do not complete their erasure. Each held instance keeps its key. Each one needs an
  audited re-drive.
- **Unresolved sweeps.** `sweep.instances{state=unresolved}` counts aged instances that your resolver
  could not map to a target. Their keys stay until you resolve them. Investigate each one.
- **Step failure rate.** A sustained `steps.failed` rate shows a poison step that retries, or a
  dependency that is down.

## Monitor the maintenance loops

The erasure sweep and the maintenance loop continue to run after a transient failure. They report a
failure only through a hook that you wire. Each loop takes two callbacks:

- `onError` receives the exception and the current `LoopPassHealth`: the number of consecutive failures
  and the timestamp of the last success. On the maintenance loop, it also receives the name of the pass.
- `onPass` runs after each pass that succeeds. On `ErasureSweepLoop`, it receives the `SweepReport`. On
  the maintenance loop, it receives the name of the pass and the `LoopPassHealth`.

1. Wire `onError` to your logging and alerting.
2. Alert on `LoopPassHealth.ConsecutiveFailures` or on a stale `LastSuccess`.
3. Run the maintenance runner on one instance only.
4. Monitor that instance from outside.

These alerts find a backstop that stopped without a report, for example when its credentials expired.

## Recover a held instance

A held instance keeps its key. The held registry records it. You can recover it.

1. Enumerate the held registry.
2. Investigate the recorded failure reason. The reason contains no subject.
3. Re-drive the instance through the termination coordinator with `ReDriveAsync`.

`ReDriveAsync` runs the retention extraction again. If the extraction succeeds, it completes the shred.

The same procedure repairs a crash between `Destroy` and the prune of the index. The termination runs
again, finds that the key is already gone, and prunes the dangling edge again.

## Validate configuration at start

1. Remove all dev defaults before you ship.
2. Supply each endpoint and each secret explicitly.
3. Fail fast when a value is missing. Do not fall back to a built-in default.
4. Give the OpenBao token rights on the Transit mount only.
5. Keep the RavenDB master KEK in a KMS or an HSM. Do not keep it in the app configuration.

The example host requires an explicit OpenBao token (`PIIMAKER_OPENBAO_TOKEN`). If the token is missing,
the host fails at start. It does not use `root` as a default.

## Known operational limits

The project discloses these limits and keeps them open. Include them in your runbook.

- **No published packages, no release versions, no CI.** The packages are not published on nuget.org.
  The repository has no git tags, and the projects set no release version. The repository has no CI
  pipeline. Consume the code by project reference at a pinned commit. Run the attestation again locally. See
  [packages](../reference/packages.md).
- **Unbounded Restate step retry.** On the Restate sidecar, a step that fails retries with infinite
  backoff. It does not go to held state yet. Wire external alerting for stuck instances on Restate. See
  the failure row in the
  [runtime matrix](../reference/runtime-matrix.md#step-failure-retry-and-poison).
- **OpenBao shred finality.** Snapshot retention limits the finality of a shred on OpenBao. There is no
  client-held KEK to rotate. Thus a restore of a storage snapshot from before a destroy reverses a shred.
  Keep snapshot retention shorter than your erasure deadline. Protect the custody of the unseal keys. See
  [Make crypto-shred durable](make-crypto-shred-durable.md).
