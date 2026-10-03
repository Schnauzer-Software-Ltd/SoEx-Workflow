> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Host: all flows · Temporal · permanent server

Production runs against a permanent Temporal server, and this host does the same. The host is a web
control panel on port 5002. It connects to the server on `localhost:7233` and runs until you stop it. The
server holds the durable state. One worker for each task queue (`pii-onboard`, `pii-renew`,
`pii-offboard`) runs for the life of the host. You operate each flow from the browser, one event at a time.

- **A Onboarding**: portable flow. The waits become Temporal signals. Their sealed continuations go to
  the server at the start, and the server buffers them. The waits have a timeout. The termination hook
  shreds the key.
- **B Subscription**: portable flow. Continue-as-new renews across the periods, on the Temporal
  `ContinueAsNew`. The idempotency store is enabled, so the charge of each generation applies one time.
- **C Offboarding**: native flow. A `[Workflow]` that the consumer wrote fans out governed revocations
  across systems in parallel. An interceptor schedules the termination hook. The portable flow cannot
  express this flow.
- **D Erasure**: the **Forget subject** button sends an erasure request for the subject. The host admits
  the request, then drains it at once. The drain uses `ErasureCoordinator`. Each in-flight instance of the
  subject goes through a forced termination, then crypto-shred and an index prune.

## Requirement: a Temporal server

This host needs a Temporal server on `localhost:7233` (Docker). If the host cannot reach the server, it
prints a message and stops. `examples/dev/piimaker.sh --runtime temporal` starts the server and the host.

```bash
dotnet run --project examples/PiiMaker/Hosts/Temporal/PiiMaker.Host.Temporal.csproj
```

The host prints the address of the panel. Open it in a browser:

```
PiiMaker Temporal control panel → http://localhost:5002  (server localhost:7233)
```

## Continue-as-new carries the step sequence

Renewal (B) runs with the idempotency store. The portable-flow drivers carry the sequence of each step
across continue-as-new generations:

- Temporal and Durable Task carry it in the run input.
- Restate carries it in the suffix of its generation key.

Thus the idempotency key `(InstanceId, DtoType, Sequence)` stays unique for the full life of the instance.
A new generation never uses sequence 0 again. Thus its first step does not collide with the first step of
the previous generation. An earlier version did not carry the sequence, and this caused an infinite
continue-as-new on Temporal. `PortableContinueAsNewIdempotencyTests` now covers it.
