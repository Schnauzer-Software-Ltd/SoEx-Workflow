# Host: all flows · Durable Task · Durable Task Scheduler

Runs every flow in one continuous process against a Durable Task Scheduler (the DTS emulator on
`localhost:8080` in dev). The worker stays up for the whole run and the scheduler holds the durable
state; a person drives the flows from the browser. One task hub, both consumption models coexisting by
orchestration name.

- **A Onboarding** — native flow: a consumer-authored orchestration of governed step activities with a
  wait-for-accept, and the base-orchestrator termination activity that shreds the key.
- **B Subscription** — portable flow: continue-as-new renewal across the periods, with the idempotency
  store enabled (each generation's charge applies once).
- **C Offboarding** — native flow: an orchestration fanning out governed revocations across systems in
  parallel, with the termination hook.
- **D Erasure** — `ErasureCoordinator` "forget subject S" over in-flight instances: force-terminate →
  crypto-shred + index prune, with a subject-level report.

## Requires a Durable Task Scheduler

This host needs a Durable Task Scheduler on `localhost:8080` (the DTS emulator, Docker). If none is
reachable it prints a message and exits.

```bash
dotnet run --project examples/PiiMaker/Hosts/DurableTask/PiiMaker.Host.DurableTask.csproj
```

## Note: continue-as-new carries the step sequence

Renewal (B) runs with the idempotency store. The portable-flow drivers carry the per-step sequence
across continue-as-new generations (Durable Task via the run input), so the idempotency key
`(InstanceId, DtoType, Sequence)` stays unique for the instance's whole life — a fresh generation never
reuses sequence 0 and collides with the previous generation's first step.
