> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Host: all flows · Durable Task · Durable Task Scheduler

This host runs all flows against a Durable Task Scheduler. In development, this is the DTS emulator on
`localhost:8080`. The host is a web control panel on port 5003. It runs until you stop it. The worker runs
for the life of the host, and the scheduler holds the durable state. You operate each flow from the
browser, one event at a time. The host uses one task hub. The
orchestration name keeps the two consumption models apart.

- **A Onboarding**: native flow. An orchestration that the consumer wrote runs governed step activities
  and a wait for the accept. The termination activity of the base orchestrator shreds the key.
- **B Subscription**: portable flow. Continue-as-new renews across the periods. The idempotency store is
  enabled, so the charge of each generation applies one time.
- **C Offboarding**: native flow. An orchestration fans out governed revocations across systems in
  parallel. It has the termination hook.
- **D Erasure**: the **Forget subject** button sends an erasure request for the subject. The host admits
  the request, then drains it at once. The drain uses `ErasureCoordinator`. Each in-flight instance of the
  subject goes through a forced termination, then crypto-shred and an index prune.

## Requirement: a Durable Task Scheduler

This host needs a Durable Task Scheduler on `localhost:8080` (the DTS emulator, Docker). The host
connects to task hub `default` with no authentication. If the host cannot reach the scheduler, it prints a
message and stops. The message gives the command that starts the emulator:
`docker run -p 8080:8080 mcr.microsoft.com/dts/dts-emulator`. Alternatively,
`examples/dev/piimaker.sh --runtime durabletask` starts the emulator and the host.

```bash
dotnet run --project examples/PiiMaker/Hosts/DurableTask/PiiMaker.Host.DurableTask.csproj
```

The host prints the address of the panel. Open it in a browser:

```
PiiMaker DurableTask control panel → http://localhost:5003  (scheduler localhost:8080)
```

## Continue-as-new carries the step sequence

Renewal (B) runs with the idempotency store. The portable-flow drivers carry the sequence of each step
across continue-as-new generations. Durable Task carries it in the run input. Thus the idempotency key
`(InstanceId, DtoType, Sequence)` stays unique for the full life of the instance. A new generation never
uses sequence 0 again. Thus its first step does not collide with the first step of the previous
generation.
