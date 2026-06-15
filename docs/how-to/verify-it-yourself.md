> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# How-to — verify it yourself

You don't need the project's own test suite to reproduce what SoEx.Workflow does. The public
[`examples/`](../../examples) are a complete, runnable consumer composition (a SoEx system wired onto
all six runtimes), so you can exercise the behaviour end to end and judge the guarantees for yourself.

This guide covers setup and the non-obvious traps. It doesn't tell you what to verify; that's your
call, and the [explanations](../README.md#explanation) describe what each guarantee means. Its job is
to make sure that whatever you check, the result means what you think it means: that a green run didn't
quietly skip the thing you cared about, and a red run isn't just a backend that wasn't ready.

## What to run

- The example hosts. `examples/PiiMaker/Hosts/<Runtime>` each stand up the same consumer system as a
  small web control panel and expose plain HTTP (`POST /IMembershipEntry/…`, `GET /example/status/{id}`
  returning `{keyLive}`, `POST /example/erase`, `GET /example/host`). Drive them however you like.
- A worked end-to-end exercise.
  [`examples/dev/smoke-all-hosts.sh`](../../examples/dev/smoke-all-hosts.sh) runs one full path — start
  an instance, observe its per-instance key, request erasure, observe the key gone — across every
  runtime, and is written to survive the timing traps below. It's a starting point you can read and
  adapt, not the limit of what's worth checking.
- Builds: `dotnet build SoEx.Workflow.sln` and `examples/SoEx.Workflow.Examples.sln`. If you exercise
  Restate, also `cargo build --release` in `src/SoEx.Workflow.Restate/restate-sidecar-rs` (see trap 1).

## Coverage depends on which backends are up

InProc needs no infrastructure but exercises the least. Every durable runtime is only exercised when
its backend is running, so a green result on a bare machine certifies a reduced subset, not the whole
thing. Any report should account for this. An absence of failures with backends down is not full
verification, just silence about the paths you didn't run, so say which backends were up: "passed"
means very different things with and without Temporal, Durable Task, Restate, Camunda 8, and a durable
key store present.

| Runtime | Needs | Default port(s) |
|---|---|---|
| InProc | nothing | — |
| Temporal | a Temporal server | 7233 |
| Durable Task | the DTS emulator (or a scheduler) | 8080 |
| Restate | restate-server, plus `cargo` to build the sidecar | 8088 (ingress), 9070 (admin) |
| Elsa | nothing (SQLite file) | — |
| Camunda 8 / Zeebe | the broker + its REST/Operate API | 26500 (gRPC), 8090 (REST) |
| durable key store | OpenBao or RavenDB (for persistence/restart checks) | 8200 (OpenBao) |

## The traps that produce false reports

These are environment and timing effects, not bugs, but each one will hand you a wrong answer if you
don't know about it.

1. **A stale Restate sidecar runs old code silently.** The Restate path runs a compiled Rust binary
   out-of-process. If you change anything and don't rebuild it (`cargo build --release`), a stale
   binary keeps serving the previous contract and your "result" reflects code that no longer exists.
   Treat a present-but-broken sidecar build as a failure, never a pass, because it certifies nothing.

2. **On Temporal, the first step runs a beat after you start.** Starting an instance returns
   immediately and mints its per-instance key, but the subject index that erasure routes by is
   populated only when the first governed step actually runs on the worker, roughly a second or two
   later on Temporal (the worker has to pick the workflow up). Erase or inspect in that gap and you'll
   see "nothing happened" and wrongly conclude it doesn't work. Wait for the first step (or retry)
   before judging. The other runtimes run the first step near-instantly, which is why this one
   surprises people.

3. **Zeebe's web endpoint is ready before its broker is.** The host accepts HTTP before the broker
   will accept the first process-instance creation, so the very first start can time out even though
   everything is fine. Retry the first start until it's accepted.

4. **Crypto-shred only holds with a durable, shared key store.** The in-memory key store is in-process
   only. Verifying that a shred survives a process restart with it will (correctly) show no
   persistence, which is not evidence about the design. Use a durable store (OpenBao or RavenDB) for
   any persistence, restart, or cross-process claim.

5. **Port-open is not backend-ready.** A control panel listens before its backend is query-ready, and
   a container's port opens before the service inside it answers. Poll a readiness signal (a
   successful call), not just the socket, before driving anything.

6. **Don't `pkill -f` a pattern that also matches your own command.** You'll kill the shell or script
   doing the killing, mid-run, and misread the fallout as a failure. Stop hosts by PID or by the port
   they hold.

7. **Two Durable Task workers on one task hub steal each other's work.** If you run an example host
   and a second Durable Task workload against the same task hub, each can pick up the other's
   activities and fail in confusing ways. Give them separate task hubs.

## Reading a result without overclaiming

Zero failures is the only pass. A skipped leg means its backend was unreachable, and that's a gap
rather than a pass for that path; chase the backend rather than reporting green. Match the claim to the
setup, too: a persistence or restart conclusion needs a durable store up, an in-memory run says nothing
about it, and a cross-runtime conclusion needs those runtimes' backends up. A reproducible red is a
true signal, but first rule out the traps above (especially 1–3), which account for most "it doesn't
work" reports that turn out to be setup.

See also the per-runtime semantics and the conditional-coverage note in the
[runtime matrix](../reference/runtime-matrix.md#verifying-locally).
