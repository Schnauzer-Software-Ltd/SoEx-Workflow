> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Crypto-shred and erasure

SoEx.Workflow can forget a workflow instance. Crypto-shred and erasure make this possible. This page
describes how they work, what they protect, their limits, and the threat model.

## The principle of crypto-shred

Crypto-shred forgets data when it destroys the key that encrypts the data. Each workflow instance creates
its own encryption key, the per-instance key. The instance seals all the data that it persists with this
key. To forget the instance, the framework destroys the key and leaves the data as it is. Without the key,
the ciphertext in the journal, in the replicas, and in the data backups is unreadable.

A durable workflow keeps its data in many places. The journal is event-sourced. The runtime replicates
it, takes snapshots of it, backs it up, and sometimes replays it. It is impractical to find and delete
each byte of the data, including the bytes in backups from before the request. Crypto-shred changes this
task to one operation: delete one key in one place. The key store and its own backups then hold the
remaining risk. See [the key store's own backups](#the-key-stores-own-backups).

The other sections of this page give the conditions that keep this operation complete.

## The termination lifecycle

The framework forgets an instance at its termination. The termination is the end of a workflow
instance. An instance terminates when it completes, when it is cancelled, or when an erasure request
erases it. At the termination, `GovernedTermination` runs a fixed sequence:

```
OnRetaining ──▶ destroy the per-instance key (crypto-shred) ──▶ prune the subject index ──▶ OnTerminated
```

1. `OnRetaining` runs first, while the data is still readable. The law can require you to keep some
   data, for example a lawful-basis record or an audit marker. In `OnRetaining`, extract that data and
   write it to your own store.
2. If `OnRetaining` succeeds, the framework destroys the per-instance key.
3. The framework prunes the subject from the subject index.
4. `OnTerminated` does the bookkeeping after the shred.

The data that you retain must be PII-free. Do not write it back into the workflow state, because the
shred makes that state unrecoverable. Do not write it into the result, because the journal keeps the
result in clear text.

If the extraction fails after its retry boundary, the framework holds the instance (quarantine). The
framework keeps the key, stops the automatic retry, and flags the instance for an audited re-drive. The
framework thus records the unmet retention obligation, and the obligation stays visible. See
[Run erasure maintenance](../how-to/run-erasure-maintenance.md).

## The sequence is one synchronous call

The utility calls your entrypoint one time, synchronously, for the full sequence. The framework destroys
the key only after `OnRetaining` returns successfully. This order is the guarantee. The data stays
readable until your retention confirms that it captured the data that it needs. After that, the data is
unreadable. The utility uses the outcome of the call to make its next decision:

- A clean return means that the retention succeeded. The shred is then safe, and the utility does it.
- An exception means that the retention failed. The utility keeps the key and holds the instance.

The framework puts no queue between the utility and the entrypoint. This rule applies also when the
entrypoint is slow or is in a different process. A queue breaks the guarantee in three ways:

- **The shred can occur before the retention completes.** A queued message gets no reply. The utility
  cannot know when, or if, `OnRetaining` completed. The utility then destroys the key without a wait,
  and the must-retain data is lost permanently. Alternatively, the utility waits for a separate
  acknowledgement message. That message is a different call, and it causes the next two problems.
- **A lost acknowledgement reports a false success.** The acknowledgement can fail to arrive, for
  example because of a dropped message or a crashed consumer. The key then stays live and the request
  looks complete. The erasure does not occur, or the framework reports it as complete. The synchronous
  call has no such gap, because the result of the call is the acknowledgement.
- **The queue adds an in-flight exposure.** The retention message goes through a broker while the key
  is live. The broker is a new hop where a person can see or keep the data, or a diagnostic that
  contains the data. Crypto-shred is a data-at-rest guarantee. It does not cover data in flight through
  a broker. See [the threat model](#threat-model).

Load and slow callers belong at the request boundary. The `External` face is designed for this. A
"forget subject S" request is idempotent and re-drivable. When you send it again, it re-drives each
instance that still needs a shred. The sweep finds each instance that the requests miss. You can queue,
batch, or retry incoming requests. Only the termination sequence must stay one confirmed call.

## Erasure requests and the subject index

A "forget subject S" request names a person. The subject index maps each PII subject to the instances
that use it. `ErasureCoordinator` receives a request and uses the index to find each targeted instance.
For each instance, the coordinator makes one of two decisions:

- If the instance is bounded and will erase itself before the statutory deadline, the coordinator lets
  it finish.
- If not, the coordinator force-terminates the instance now.

The coordinator drives the force-terminations to crypto-shred. It then reports the result at the
fidelity that it achieved.

The `ReceivedAt` value of the request starts a statutory clock. The coordinator uses this clock to flag
a request that is at risk of a breach of its legal deadline.

A flow can touch more persons than the person for whom it started. For example, a step that reads an
account can learn its billing contact, and a claim can name a dependant. The step declares these
subjects on the action that it returns (see
[`WorkflowAction`](../reference/workflow-action.md#enrolling-a-subject-the-step-learned)). The framework
adds them to the index at that moment, before the flow moves to its next step. This timing is
important. An instance can wait for days. Until the index entry exists, the only record of that person
is in a sealed continuation, and an erasure request cannot look there. To read the mapping in the other
direction (the instances that hold a subject), use `InstancesForAsync` on the subsystem face of the
utility.

## What is sealed vs guarded

Crypto-shred protects the data that the framework seals with the key. The runtime must read two types of
value in clear text, so the framework does not seal them:

- Runtime-visible names: the instance id, the event names, and the raise id. The runtime routes on these
  names, so they cannot be ciphertext.
- Step results and workflow results. The final result goes back to the caller in clear text. In a
  native flow, the runtime journals the result of each step in clear text when it passes the result to
  the next step. In the portable flow, the driver seals what it journals, so only the final result is
  in clear text.

These values stay after the shred, so the framework guards them. The guard rejects these values if they
contain a subject id:

- the instance id
- the event name of each branch of a portable wait, and the event name of a raise through the workflow
  utility
- the raise id, if the gateway has a `GatewaySealGuard`
- the final result of the portable flow
- a native step result

The framework refuses a non-null native step result by default. A binding opts in with
`clearJournalResult: true`, and then the guard scans the result. The framework also scans the exception
text that the journal or the held log keeps for a failed step. If that text contains a subject id, the
framework withholds it.

The guard does not scan timers, the event names that a native flow waits on through the runtime API, or
an event name that you pass directly to `IWorkflowGateway.RaiseEventAsync`. A portable timer has a
duration and no name. Keep these values PII-free.

The guard checks forward only. It checks each name against the subjects that are known when the name is
written. A subject that a step enrolls during the flow is guarded from that step onward. The guard does
not examine again the names that the instance journaled earlier. A name written before the flow knew of
a person cannot contain that person. For this reason, the framework adds an enrollment to the context of
the step *before* it flattens and guards the action of that step.

By default, the guard is a substring scan for the subject ids that SoEx governs. Its scope is narrow by
design. It stops an accidental leak of a subject that you told the framework about. It finds only the
subject ids that the framework knows, and it is not a general PII scanner. Your primary defense is to
keep these values PII-free by design:

- Derive instance ids with `DeterministicInstanceId` (a hash, never the email).
- Name events and timers by a PII-free kind.
- In `OnRetaining`, write must-retain PII to your own store. Do not write it into the result.

To make the guard find more, replace the `ISubjectMatcher`. It is pluggable. You can supply a regex, an
NER model, or a denylist. See [Customize PII detection](../how-to/customize-pii-detection.md).

## What makes the shred hold: a durable, shared key store

The shred is complete only if the destroyed key was the only copy. Two requirements follow.

First, the persisted bytes must be ciphertext. In the portable flow, the driver seals all the data that
it journals, so this is automatic. In a native flow, this is your one duty: persist only the sealed seed,
and unseal it only inside a step.

Second, the key store must be durable. It must also be shared by each process that runs an instance: its
client, its orchestrator, and its step workers. The bundled `InMemoryInstanceKeyStore` shreds within one
process only. Use it for tests and demos. In production, use a bundled durable store (OpenBao or RavenDB)
or your own store. If the key store is not shared, a destroyed key can stay in the memory of a different
process. The shred then did not occur. See
[Make crypto-shred durable](../how-to/make-crypto-shred-durable.md).

## Backstops for abandoned instances

The termination hook shreds an instance on its normal paths. An instance can become abandoned before its
hook runs. Two causes are possible:

- A hard worker death at the instant of the termination.
- An admin `terminate` or `purge` that skips the flow code.

Two backstops close this gap:

- A later erasure request for the subject re-drives each instance that is still in the index and not
  terminated. A filed request thus closes the gap when the data of the subject is erased.
- A sweep checks the age of the live key set, independently of requests. It force-terminates each
  instance that is older than a threshold. The sweep thus shreds an abandoned instance also when its
  subject never files a request.

[Run erasure maintenance](../how-to/run-erasure-maintenance.md) describes both backstops.

## Threat model

Crypto-shred is a data-at-rest erasure guarantee. It protects against an adversary who reads the durable
journal, its replicas, or its data backups after the shred. That adversary sees only ciphertext for a
key that no longer exists.

The guarantee has these limits:

- While the key is live, the data is readable. A running workflow must read its data. Crypto-shred gives
  no protection against an adversary who reads the data before the shred.
- A key store that keeps a copy after `Destroy` defeats the shred. For this reason, the key store must
  be durable and shared.
- PII that you journal in clear text stays readable. For this reason, the framework guards names and
  results, and you write retained data to your own store.
- Crypto-shred is not an access-control mechanism. It does not protect data in flight.

Transport security is a separate subject. Put TLS on each connection of an adapter that leaves the host.
TLS keeps tokens and credentials safe in flight. See
[Transport security](../reference/transport-security.md).

These limits give you real obligations. [Secure a PII deployment](../how-to/secure-a-pii-deployment.md)
puts them in a pre-production checklist.

### Logs and telemetry

Crypto-shred covers the durable journal and its backups. Your logs and traces are outside it. When a step
throws an exception, the host framework reports the failure through its normal diagnostic paths. The
message of a consumer exception can contain the subject. Without protection, that message carries the
subject into telemetry. There it is outside the crypto-shred boundary, and a later `Destroy` has no
effect on it.

The framework sends each part that can carry a subject through a telemetry-confidentiality component.
You set this component on the pipeline. These parts go through it:

- The error log of a failed step.
- The error log of the endpoint pipeline.
- The values that the framework attaches to log scopes and trace tags.

The dispatcher records no exception text on its span. It marks the span as failed and records nothing
more. The durable journal stays in the shred boundary in all cases.

The default telemetry-confidentiality component redacts. It logs an exception message as `[redacted]`
and a scope or tag value as `[redacted:<type>]`. A subject in an exception message thus stays out of logs
and traces. A development component replaces redaction with a pass-through.

> [!WARNING]
> Do not run the pass-through development component in production. With that component, a subject in an
> exception message goes into logs and traces in clear text.

The example hosts use the default. They set the base default pipeline as the system default. Each host
thus redacts on the error path with no custom code. See `Defaults` in
`examples/PiiMaker/Hosts/Common/MembershipSystem.cs`. The exception *type* and the stack trace still go
into telemetry. They contain frame names and type names, and they do not contain the subject value. Keep
subjects out of type names, log messages, log scopes, span attributes, and metric tags by design.

### The key store's own backups

The key store holds the per-instance keys, and the key store has its own backups. `Destroy` removes the
key from the live store. A key-store snapshot or backup taken before the destroy still holds the key.

- **RavenDB.** A long-lived master KEK wraps each per-instance key. A retained pre-destroy snapshot and
  the KEK together can reverse a shred.
- **OpenBao.** OpenBao keeps the key material on the server. Its equivalent risk is an OpenBao storage
  or Raft snapshot. It has no client-held KEK.

Per-instance keys seal the data backups, so the data backups are unreadable after a shred. Key-store
backups contain the keys, so this rule does not apply to them.

The bundled mitigations are operational:

- Limit the retention of key-store snapshots and backups. No snapshot must live longer than the erasure
  window of the instance. A snapshot that you no longer hold cannot reverse a shred.
- Keep the RavenDB master KEK in a KMS or HSM. Do not keep it in the app configuration. A leaked data
  or key snapshot alone is then not sufficient.
- After a batch of shreds, call `RavenDbInstanceKeyStore.RotateKek`. When you rotate out and retire the
  old KEK, the wrapped per-instance keys in older snapshots become permanently unwrappable.

[Make crypto-shred durable](../how-to/make-crypto-shred-durable.md) shows where to configure these
mitigations.

### Deserialization safety rests on the seal

The seal protects the deserializer as well as the erasure. SoEx serializes the journal payloads with the
`IMessageSerializer` of the host. The default SoEx serializer binds each value to its declared type and
to an explicit list of known types. A host can select the polymorphic Newtonsoft serializer. That
serializer records the .NET type of each value, so that it can restore the type. This is a known
deserialization-gadget surface. If attacker-controlled bytes get to the deserializer, a forged type tag
can create an unexpected type.

In SoEx.Workflow, attacker-controlled bytes cannot get to the deserializer. The framework decrypts and
authenticates each externally-influenceable payload with AES-256-GCM under the per-instance key. It does
this before it deserializes the payload. To forge a payload, an attacker needs the per-instance key, and
the attacker does not have it. The authenticated encryption that makes a shred final thus also controls
all data that gets to the deserializer. The seal is load-bearing for injection safety and for erasure.

For defense-in-depth on the Newtonsoft serializer, use the pluggable serializer seam. Stay on the
default serializer, or supply your own `IMessageSerializer` that pins a type allowlist (a
`SerializationBinder`). This closes the gadget surface also for a forged in-envelope type, a case that
the design makes impossible. SoEx.Workflow includes no such binder, for two reasons:

- The host supplies the serializer, and SoEx.Workflow does not configure it.
- The seal already closes the reachable path.

## See also

- [Governance design](governance-design.md): the key, the index, and idempotency as one system.
- [Erasure API reference](../reference/erasure-api.md) and
  [erasure events](../reference/erasure-events.md).
