> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Crypto-shred and erasure

The defining feature of SoEx.Workflow is that a workflow instance can be forgotten. This page explains
how that works, what it does and doesn't protect, and the threat model behind it.

## Why crypto-shred, not find-and-delete

The obvious way to forget someone's data is to find every copy and delete it. In a durable workflow
that approach doesn't hold up: the data is spread across an event-sourced journal, replicated,
snapshotted, backed up, and sometimes replayed. Reliably locating and deleting every byte, including in
backups taken before the request, is impractical at best.

Crypto-shred inverts the problem. Every instance mints a per-instance encryption key, and everything the
instance persists is sealed under it. To forget the instance you don't touch the data at all; you
destroy the one key. Without it, the ciphertext sitting in the journal, the replicas, and the data
backups is all equally unreadable. This reduces "delete data everywhere" to "delete one key in one
place", and leaves the key store and its own backups as the remaining concern (see
[the key store's own backups](#the-key-stores-own-backups) below).

Everything else on this page is about making that reduction hold.

## The termination lifecycle

Forgetting happens at the termination: the end of a workflow instance, whether it completed naturally,
was cancelled, or is being erased on request. At that moment `GovernedTermination` runs a fixed
sequence:

```
OnRetaining ──▶ destroy the per-instance key (crypto-shred) ──▶ prune the subject index ──▶ OnTerminated
```

`OnRetaining` fires first, while the data is still readable, because sometimes you're legally required
to keep something (a lawful-basis record, an audit marker). That's your chance to extract it and write
it outward to your own store. What you retain must be PII-free, and it must not go back into workflow
state or the result: the former is about to become unrecoverable, and the latter is journaled in clear.
After `OnRetaining` succeeds the key is destroyed and the subject pruned; `OnTerminated` then does
post-shred bookkeeping.

If extraction fails past its retry boundary, the instance is held (quarantined): the key is kept,
auto-retry stops, and the instance is flagged for an audited re-drive, so the unmet retention
obligation is recorded rather than silently lost. See
[Run erasure maintenance](../how-to/run-erasure-maintenance.md).

## Why the sequence runs synchronously

That sequence runs as a single synchronous call from the utility into your entrypoint, and the key is
destroyed only after `OnRetaining` returns successfully. The ordering is the guarantee: the data stays
readable until your retention has confirmed it captured what it needed, and no longer. The utility uses
the call's outcome to make the next decision. A clean return means retention succeeded, so the shred is
safe to perform; an exception means it did not, so the key is kept and the instance is held.

This is worth calling out because the instinct, when an entrypoint is slow or lives in another process,
is to decouple the call by putting a queue between the utility and the entrypoint. That breaks the
guarantee in three ways, so the framework does not do it:

- **The shred can race the retention.** A queued message is fire-and-forget: the utility no longer
  learns when, or whether, `OnRetaining` finished. It would either destroy the key without waiting,
  before the must-retain data was extracted and so losing it for good, or wait for a separate
  acknowledgement message, which is no longer the same call and brings the next two problems.
- **A lost acknowledgement reports success that never happened.** If the shred waits on an ack that
  never arrives (a dropped message, a crashed consumer), the instance sits with its key still live while
  the request looks done. The erasure silently doesn't happen, or worse is reported complete when it
  wasn't. The synchronous call has no such gap: its result *is* the acknowledgement.
- **It opens a new in-flight exposure.** The retention message crosses a broker while the key is still
  live, adding a hop where the data, or a diagnostic carrying it, can be observed or retained.
  Crypto-shred is a data-at-rest guarantee; a broker in the shred path adds an in-flight surface it was
  never meant to cover (see [the threat model](#threat-model)).

The place to absorb load or decouple from a slow caller is the request boundary, not the shred. The
`External` face is shaped for exactly that: a "forget subject S" request is idempotent and re-drivable,
so re-issuing it re-drives any instance that still needs shredding, and the sweep catches anything
missed. Requests coming in are safe to queue, batch, or retry. The one thing that must stay a single
confirmed call is the termination sequence itself.

## Driving erasure: requests and the subject index

A "forget subject S" request names a person, not an instance. To act on it, SoEx keeps a subject index
mapping each PII subject to the instances touching it. `ErasureCoordinator` takes a request, fans out
across the index to every targeted instance, and decides per instance whether to let it finish naturally
(if it's bounded and will self-erase before the statutory deadline) or force-terminate it now. It then
drives the force-terminations to crypto-shred and reports at whatever fidelity it achieved.

The request's `ReceivedAt` anchors a statutory clock, so the coordinator can flag a request at risk of
breaching its legal deadline.

## What is sealed vs guarded

Crypto-shred only protects what was sealed under the key. Two things are deliberately not sealed,
because the runtime needs to read them in clear, and those get a different treatment:

- Runtime-visible names: the instance id and event/timer names. The engine routes on these, so they
  can't be ciphertext.
- Step and workflow results. The final result is returned to the caller in clear; and in a native flow,
  each step's result is journaled in clear by the engine as it passes between steps (in the portable flow
  the driver seals what it journals, so only the final result is exposed this way).

Because these escape the shred, the framework guards them instead: it rejects any instance id or step or
final result that carries a subject id, on both the portable flow and the shared native dispatch path.
(Timers carry no guarded id.)

By default that guard is a substring scan for the subject ids SoEx already governs. Its scope is
deliberately narrow: it stops you accidentally leaking a subject you already told the framework about,
and it makes no attempt to be a universal PII scanner. Your primary defense is to keep these values
PII-free by construction. Derive instance ids with `DeterministicInstanceId` (a hash, never the email),
name events and timers by PII-free kind, and write must-retain PII outward in `OnRetaining` rather than
into the result. Where you want the guard to catch more, it's a pluggable `ISubjectMatcher`; supply a
regex, an NER model, or a denylist (see
[Customize PII detection](../how-to/customize-pii-detection.md)).

## What makes the shred hold: a durable, shared key store

The shred holds only if the destroyed key was the only copy. Two requirements follow from that.

First, the persisted bytes must actually be ciphertext. In the portable flow that's automatic, because
the driver seals everything it journals. In a native flow it's your one duty: persist only the sealed
seed, unseal only inside a step.

Second, the key store must be durable and shared across every process that runs an instance: its
client, orchestrator, and step workers. The bundled `InMemoryInstanceKeyStore` shreds only within one
process, so it's for tests and demos; production uses a bundled durable store (OpenBao or RavenDB) or
your own. If the key store isn't shared, a "destroyed" key might survive in another process's memory,
and the shred wouldn't actually have happened. See
[Make crypto-shred durable](../how-to/make-crypto-shred-durable.md).

## Closing the gaps: the backstops

The termination hook shreds an instance on its happy paths. But an instance can be abandoned before its
hook ever runs: a hard worker death at the termination instant, or an admin `terminate`/`purge` that
bypasses flow code. Two mechanisms close that gap:

- A later erasure request for the subject re-drives any still-indexed, un-terminated instance, so a
  filed request closes the gap whenever the subject's data is actually erased.
- A request-independent sweep ages the live key set and force-terminates anything older than a
  threshold, so an abandoned instance whose subject never files a request is still shredded.

Both are covered in [Run erasure maintenance](../how-to/run-erasure-maintenance.md).

## Threat model

Crypto-shred defends against an adversary who reads the durable journal, its replicas, or its data
backups after the shred: they see only ciphertext for a key that no longer exists. It does not defend
against an adversary reading the data before the shred: while the key is live, the data is readable,
which is the whole point of a running workflow. Nor does it defend against a key store that keeps a
copy after `Destroy` (hence the durable-shared requirement), or against you yourself journaling PII in
the clear (hence the name/result guards and the "write outward" discipline). It is a data-at-rest
erasure guarantee, not an access-control or confidentiality-in-flight mechanism. Securing the network hops
the adapters use — putting TLS on the connections that leave the host, so tokens and credentials aren't
exposed in flight — is a separate concern, covered in [Transport security](../reference/transport-security.md).

The boundaries below leave real obligations with you. [Secure a PII deployment](../how-to/secure-a-pii-deployment.md)
collects them into a pre-production checklist.

### Logs and telemetry

The shred covers the durable journal and its backups, not your logs or traces. When a step throws, the host
framework reports that failure through its normal diagnostic paths, and a consumer exception whose message
embeds the subject would otherwise carry it into telemetry, outside the crypto-shred boundary and unaffected
by a later `Destroy`. The framework now routes the parts that could carry a subject through a
telemetry-confidentiality component you set on the pipeline: the failed-step error log, the endpoint
pipeline's error log, and the values it attaches to log scopes and trace tags all pass through it, and the
dispatcher no longer records the exception text on its span (it marks the span failed and nothing more). The
durable journal stays shred-covered regardless; this is a telemetry concern, not a journal one.

The default telemetry-confidentiality component redacts: an exception message logs as `[redacted]` and a
scope or tag value as `[redacted:<type>]`, so a subject in an exception message does not reach logs or traces
in clear unless you replace that component with a pass-through one (there is a development component that does
exactly that, which you should not run in production). The example hosts rely on this: they set the base
default pipeline as the system default, so every host redacts on the error path with no custom code (see
`Defaults` in `examples/PiiMaker/Hosts/Common/MembershipSystem.cs`). What still escapes is the exception
*type* and stack trace, which name frames and types rather than the subject value, so keep subjects out of
type names, log messages, log scopes, span attributes, and metric tags by construction.

### The key store's own backups

The "delete one key" reduction hides a subtlety: that key lives in the key store, and the key store is
itself backed up. `Destroy` removes the key from the live store, but a key-store snapshot or backup
taken before the destroy still holds it. For the RavenDB store the master KEK that wraps every
per-instance key is long-lived, so a retained pre-destroy snapshot plus the KEK can reverse a shred.
(OpenBao keeps key material server-side, so its analogue is an OpenBao storage/Raft snapshot rather than
a client-held KEK.) This is the one place the "backups are all equally unreadable" intuition breaks: it
holds for data backups, sealed under per-instance keys, but not for key-store backups.

The mitigations are operational and bundled:

- Bound key-store snapshot/backup retention so no snapshot outlives the instance's erasure window; a
  snapshot you no longer hold cannot reverse a shred.
- Keep the RavenDB master KEK in a KMS/HSM rather than in app config, so a leaked data/key snapshot alone
  is not enough.
- After a batch of shreds, call `RavenDbInstanceKeyStore.RotateKek`: once the old KEK is rotated out and
  retired, the wrapped per-instance keys captured in older snapshots become permanently unwrappable.

See [Make crypto-shred durable](../how-to/make-crypto-shred-durable.md) for where these are wired.

### Deserialization safety rests on the seal

The journal payloads SoEx persists are serialized with the host's `IMessageSerializer`. The default
SoEx serializer is polymorphic (it records each value's .NET type so it can round-trip the type back),
which is a classic deserialization-gadget surface: if attacker-controlled bytes ever reached the
deserializer, a forged type tag could instantiate an unexpected type. In SoEx.Workflow they do not,
because every externally-influenceable payload is AES-256-GCM authenticated-decrypted under the
per-instance key before it is deserialized, so forging a payload requires the per-instance key, which an
attacker does not have. The seal is therefore load-bearing for injection safety as well as for erasure:
the same authenticated encryption that makes a shred final also gates what ever reaches the
deserializer.

For defense-in-depth, the serializer is a pluggable seam: supply your own `IMessageSerializer` that pins
a type allowlist (a `SerializationBinder` for the Newtonsoft serializer, or a non-polymorphic serializer
for your DTOs), and the gadget surface closes even under the impossible-by-design case of a forged
in-envelope type. SoEx.Workflow does not ship such a binder by default because it does not configure the
serializer (the host supplies it) and because the seal already closes the reachable path.

## See also

- [Governance design](governance-design.md) — the key, index, and idempotency as a system.
- [Erasure API reference](../reference/erasure-api.md) and
  [erasure events](../reference/erasure-events.md).
