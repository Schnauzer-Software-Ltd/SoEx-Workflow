# Secure a PII deployment

SoEx.Workflow gives you one thing: data-at-rest erasure of the durable journal, through crypto-shred. The
rest of a safe deployment is yours. The threat model in [Crypto-shred and erasure](../explanation/crypto-shred-and-erasure.md)
spells out where the guarantee stops; this page turns those boundaries into a pre-production checklist. Each
item links to the guide that covers it.

## Keys and crypto-shred

- [ ] Use a durable, shared key store (OpenBao or RavenDB), not the in-memory one. The in-memory store
  shreds only within a single process, so a "destroyed" key can survive elsewhere. See
  [Make crypto-shred durable](make-crypto-shred-durable.md).
- [ ] Hold the master KEK in a KMS/HSM, not in app config, so a leaked data or key snapshot is not enough on
  its own. See [Make crypto-shred durable](make-crypto-shred-durable.md).
- [ ] Bound key-store snapshot and backup retention below your erasure window. A pre-destroy snapshot of the
  wrapped key plus the KEK can reverse a shred. After a batch of shreds, rotate and retire the KEK
  (`RotateKek`). See the threat model in [Crypto-shred and erasure](../explanation/crypto-shred-and-erasure.md)
  and [Governance services](../reference/governance-services.md).

## Keep clear-journaled values PII-free

The instance id, event and timer names, and the workflow result are journaled in clear and escape the shred.
The framework guards them against the subjects it already knows, but the primary defence is to keep them
PII-free by construction.

- [ ] Derive instance ids with `DeterministicInstanceId` (a hash), never from an email or other subject. See
  [Triggering](../reference/triggering.md).
- [ ] Name events and timers by a PII-free kind.
- [ ] Write must-retain PII outward in `OnRetaining`, never into a step or workflow result. See
  [Erasure events](../reference/erasure-events.md).
- [ ] Tighten the subject matcher if you want the guard to catch more than the subjects you already declare.
  See [Customize PII detection](customize-pii-detection.md).

## Telemetry

Logs and traces are outside the shred boundary, so a subject that reaches them survives erasure.

- [ ] Keep a redacting telemetry-confidentiality component on the pipeline. The framework default redacts
  exception messages and scope/tag values on the error path with no custom code, so do not swap in the
  pass-through (development) component in production. See the "Logs and telemetry" section of
  [Crypto-shred and erasure](../explanation/crypto-shred-and-erasure.md).
- [ ] Keep subjects out of type names, log scopes, span attributes, and metric tags. The exception type and
  stack trace are still emitted.

## Transport and access

- [ ] Put TLS on every network hop that leaves the host. See [Transport security](../reference/transport-security.md).
- [ ] Supply gateway authentication and authorization; the framework performs none. Make instance ids
  unguessable. See [Authorize the gateway seam](authorize-the-gateway-seam.md).

## Delivery and step failure

- [ ] Wire a durable idempotency store (not the in-memory one) and set `StealAfter` longer than your slowest
  step; make your step effects idempotent. Delivery is effectively-once, at-least-once under a crash in the
  effect-commit-to-done-write window. See [Governance services](../reference/governance-services.md).
- [ ] Understand the step-failure policy: a step is retried under a bounded policy and, when its attempts are
  spent, the instance is **parked** with its key retained (not lost). Monitor the held count and re-drive held
  instances. On Restate a failing step still retries unbounded — alert on stuck instances there. See the step-
  failure row in the [Runtime matrix](../reference/runtime-matrix.md#step-failure-retry-and-poison).

## Operations and certification

- [ ] Run erasure maintenance (the sweep, held re-drive, and deadline review) on exactly one instance so an
  instance abandoned before its termination hook ran is still shredded. See
  [Run erasure maintenance](run-erasure-maintenance.md).
- [ ] Wire the `SoEx.Workflow` metrics meter and alert on the backlog age, held count, and maintenance-pass
  freshness — the maintenance loops keep running across a transient failure but only tell you if you wire the
  `onError` hook. See [Operate in production](operate-in-production.md).
- [ ] Wire a durable erasure tombstone if erasure finality must survive a restart, so a raise arriving after a
  shred cannot re-mint the instance and resume the flow.
- [ ] Certify the deployment-shaped composition against real backends, not only the no-infrastructure run.
  The hermetic test set proves logical behaviour, not your wiring. See the
  [Runtime matrix](../reference/runtime-matrix.md) and [Verify it yourself](verify-it-yourself.md).
