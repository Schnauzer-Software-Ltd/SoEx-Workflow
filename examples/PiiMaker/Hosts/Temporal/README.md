# Host: all flows · Temporal · permanent server

Production runs against a permanent Temporal server, so this host does too. It runs every flow in one
continuous process (no restart) against `localhost:7233`; the server holds the durable state and the
workers stay up for the whole run.

- **A Onboarding** — portable flow: the waits become Temporal signals (their sealed continuations are
  delivered up-front and buffered), with a timeout, and the termination hook shreds the key.
- **B Subscription** — portable flow: continue-as-new renewal across the periods, on Temporal's
  `ContinueAsNew`, with the idempotency store enabled (each generation's charge applies once).
- **C Offboarding** — native flow: a consumer-authored `[Workflow]` that fans out governed revocations
  across systems in parallel, with the interceptor-scheduled termination hook. This is the one the
  portable flow can't express.
- **D Erasure** — `ErasureCoordinator` "forget subject S" over in-flight instances: force-terminate →
  crypto-shred + index prune, with a subject-level report.

## Requires a Temporal server

Unlike the other hosts, this one needs a Temporal server on `localhost:7233` (Docker). If none is
reachable it prints a message and exits.

```bash
dotnet run --project examples/PiiMaker/Hosts/Temporal/PiiMaker.Host.Temporal.csproj
```

```
▶ PiiMaker on Temporal @ localhost:7233 (permanent server) — all flows, one continuous host

A onboarding  : "assigned:res-1"  |  key live after = False  |  subject indexed = 0  |  retained outward = 1
B subscription: "renewed:3"       |  key live after = False  |  subject indexed = 0  |  retained outward = 1
C offboarding : "offboarded"      |  key live after = False  |  subject indexed = 0  |  retained outward = 1

D erasure     : 'forget ex-member@example.com' — 2 in-flight (keys live = 2, indexed = 2)
                  t-erase-a: ForceTerminate → Complete
                  t-erase-b: ForceTerminate → Complete
                  after sweep: keys live = 0, indexed = 0
```

## Note: continue-as-new carries the step sequence

Renewal (B) runs with the idempotency store. The portable-flow drivers carry the per-step sequence
across continue-as-new generations (Temporal/DurableTask via the run input; Restate via its generation
key suffix), so the idempotency key `(InstanceId, DtoType, Sequence)` stays unique for the instance's
whole life: a fresh generation never reuses sequence 0 and collides with the previous generation's
first step. (Earlier this wasn't carried, which caused an infinite continue-as-new on Temporal; it's
covered now by `PortableContinueAsNewIdempotencyTests`.)
