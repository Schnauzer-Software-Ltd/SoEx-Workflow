> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

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

## Operations and certification

- [ ] Run erasure maintenance (the sweep, held re-drive, and deadline review) so an instance abandoned before
  its termination hook ran is still shredded. See [Run erasure maintenance](run-erasure-maintenance.md).
- [ ] Certify the deployment-shaped composition against real backends, not only the no-infrastructure run.
  The hermetic test set proves logical behaviour, not your wiring. See the
  [Runtime matrix](../reference/runtime-matrix.md) and [Verify it yourself](verify-it-yourself.md).
