> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Secure a PII deployment

SoEx.Workflow erases data at rest in the durable journal. It does this with crypto-shred. You are
responsible for the other parts of a safe deployment. The threat model in
[Crypto-shred and erasure](../explanation/crypto-shred-and-erasure.md) gives the limits of this
guarantee. This page turns those limits into a checklist to complete before production. Each item links
to the guide for that item.

## Keys and crypto-shred

- [ ] Use a durable, shared key store: OpenBao or RavenDB. Do not use the in-memory key store. The
  in-memory key store shreds in one process only, so a "destroyed" key can survive in a different place.
  See [Make crypto-shred durable](make-crypto-shred-durable.md).
- [ ] Keep the master KEK in a KMS or an HSM. Do not keep it in the app configuration. Then a leaked data
  snapshot or key snapshot alone is not sufficient. See
  [Make crypto-shred durable](make-crypto-shred-durable.md).
- [ ] Keep key-store snapshots and backups for less time than your erasure window. A snapshot from before
  the destroy holds the wrapped key. That snapshot and the KEK together can reverse a shred. After a batch
  of shreds, rotate and retire the KEK (`RotateKek`). See the threat model in
  [Crypto-shred and erasure](../explanation/crypto-shred-and-erasure.md) and
  [Governance services](../reference/governance-services.md).

## Keep PII out of clear-text values

The journal keeps the instance id, the event names, the raise id, and the workflow result in clear text.
These values stay after the shred. The framework guards them against the subjects that it knows. The
guard does not scan timers or the event names that a native flow uses directly with the runtime. See
[Customize PII detection](customize-pii-detection.md) for the full list. The primary defence is to keep
PII out of these values in their design.

- [ ] Derive instance ids with `DeterministicInstanceId`, which gives a hash. Never derive an instance id
  from an email address or another subject. See [Triggering](../reference/triggering.md).
- [ ] Name events and timers by a kind that contains no PII.
- [ ] Wire a `GatewaySealGuard` on each network-reachable gateway. It rejects a plaintext seed or payload,
  and a raise id that contains a known subject. See [Triggering](../reference/triggering.md).
- [ ] Write PII that you must keep to your own store in `OnRetaining`. Never write it into a step result
  or a workflow result. See [Erasure events](../reference/erasure-events.md).
- [ ] Make the subject matcher stricter if you want the guard to catch more than your declared subjects.
  See [Customize PII detection](customize-pii-detection.md).

## Telemetry

Logs and traces are outside the shred boundary. A subject in a log or a trace stays after erasure.

- [ ] Keep a redacting telemetry-confidentiality component on the pipeline. The framework default redacts
  exception messages and scope and tag values on the error path. It needs no custom code. In production,
  do not replace it with the pass-through component, which is for development. See the "Logs and
  telemetry" section of [Crypto-shred and erasure](../explanation/crypto-shred-and-erasure.md).
- [ ] Keep subjects out of type names, log scopes, span attributes, and metric tags. The framework still
  emits the exception type and the stack trace.

## Transport and access

- [ ] Use TLS on each network hop that leaves the host. See
  [Transport security](../reference/transport-security.md).
- [ ] Supply gateway authentication and authorization. The framework does no authentication or
  authorization. Make instance ids unguessable. See
  [Authorize the gateway seam](authorize-the-gateway-seam.md).

## Delivery and step failure

- [ ] Use a durable idempotency store. Do not use the in-memory one. Set `StealAfter` to a time longer
  than your slowest step. Make the effects of your steps idempotent. Delivery is effectively-once. If a
  crash occurs between the effect commit and the done write, delivery is at-least-once. See
  [Governance services](../reference/governance-services.md).
- [ ] Learn the step-failure policy. The framework retries a step under a bounded policy. When the step
  uses all its attempts, the framework **parks** the instance and keeps its key, so the instance stays
  recoverable. Monitor the held count, and re-drive held instances. On Restate, a failed step still retries
  with no limit, so alert on stuck instances there. See the step-failure row in the
  [Runtime matrix](../reference/runtime-matrix.md#step-failure-retry-and-poison).

## Operations and certification

- [ ] Run erasure maintenance on one node only. Erasure maintenance is the sweep, the held re-drive, and
  the deadline review. It shreds an instance that was abandoned before its termination hook ran. See
  [Run erasure maintenance](run-erasure-maintenance.md).
- [ ] Wire the `SoEx.Workflow` metrics meter. Alert on the backlog age, the held count, and the freshness
  of the maintenance passes. The maintenance loops continue after a transient failure. They report the
  failure only through the `onError` hook, so wire that hook. See
  [Operate in production](operate-in-production.md).
- [ ] Wire a durable erasure tombstone if erasure finality must survive a restart. Then a raise that
  arrives after a shred cannot re-mint the instance and resume the flow.
- [ ] Certify the deployment-shaped composition against real infrastructure. Do not rely on the run with no
  infrastructure only. The hermetic test set proves logical behavior. It does not prove your wiring. See
  the [Runtime matrix](../reference/runtime-matrix.md) and [Verify it yourself](verify-it-yourself.md).
