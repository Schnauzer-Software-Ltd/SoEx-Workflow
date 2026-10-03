> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Verify it yourself

The public [`examples/`](../../examples) folder is a complete consumer composition that you can run. It
wires a SoEx system onto all six runtimes. Use it to test the behavior of SoEx.Workflow from end to end
and to judge its guarantees.

This guide gives the setup and the traps that can give a false result. You decide what to verify. The
[explanations](../README.md#explanation) tell what each guarantee means. This guide helps you read each
result correctly. A pass must include the paths that you care about. A failure can come from a server that
was not ready, so check for that first.

## What to run

1. Build the product: `dotnet build SoEx.Workflow.sln`.
2. Build the examples: `dotnet build examples/SoEx.Workflow.Examples.sln`.
3. If you test Restate, build the example sidecar in `examples/PiiMaker/Hosts/Restate/sidecar-rs`.

   Use `cargo build --release`. The Restate example host builds this sidecar only when the binary is
   absent. See trap 1. The framework sidecar in `src/SoEx.Workflow.Runtime.Restate/restate-sidecar-rs`
   is a different binary. The example hosts do not use it.
4. Start the example hosts that you need.

   Each `examples/PiiMaker/Hosts/<Runtime>` folder starts the same consumer system as a small web control
   panel. Each host gives these plain HTTP endpoints:

   - `POST /IMembershipManager/…`
   - `GET /example/status/{id}`, which returns `{keyLive}`
   - `POST /example/erase`
   - `GET /example/host`
5. Send requests to the hosts with any tool that you like.

[`examples/dev/smoke-all-hosts.sh`](../../examples/dev/smoke-all-hosts.sh) is a worked end-to-end test. On
each runtime, it does one full path:

1. Start an instance.
2. Read its per-instance key.
3. Request erasure.
4. Read the key again, and see that the key is gone.

The script handles the timing traps below. Read it and change it for your checks. It is a starting point.
You can check more than it does.

## Coverage and the servers that are up

InProc needs no infrastructure, but it tests the least. A durable runtime is tested only when its server
is up. A pass on a machine with no servers certifies a reduced subset. Each report must say which servers
were up. A pass means different things with and without Temporal, Durable Task, Restate, Camunda 8, and a
durable key store.

> [!NOTE]
> The Temporal time-skipping tests use the Temporal test server. The SDK **downloads this server on first
> use**. Thus the first run on a new machine needs network access, but no Temporal server. Later runs use
> the cached binary and need no network.

| Runtime | Needs | Default port(s) |
|---|---|---|
| InProc | nothing | — |
| Temporal | a Temporal server | 7233 |
| Durable Task | the DTS emulator (or a scheduler) | 8080 |
| Restate | restate-server, and `cargo` to build the sidecar | 8088 (ingress), 9070 (admin) |
| Elsa | nothing (SQLite file) | — |
| Camunda 8 / Zeebe | the broker and its REST/Operate API | 26500 (gRPC), 8090 (REST) |
| durable key store | OpenBao or RavenDB (for persistence and restart checks) | 8200 (OpenBao) |

## The traps that produce false reports

These traps are effects of the environment and of timing. They are not defects in SoEx.Workflow. Each
trap gives a wrong result if you do not know about it.

1. **Rebuild the Restate sidecar after each change.** The Restate path runs a compiled Rust binary out of
   process. If you do not rebuild it with `cargo build --release`, the old binary continues to serve the
   previous contract. Your result then shows code that no longer exists. If the sidecar build is present
   but broken, record a failure. That build certifies nothing.

2. **On Temporal, wait for the first step.** A start returns immediately and mints the per-instance key.
   The subject index gets its entry only when the first governed step runs on the worker. The worker must
   pick up the workflow, so on Temporal this occurs one or two seconds later. If you erase or inspect in
   that gap, nothing seems to occur, and the result looks like a defect. Wait for the first step, or retry,
   before you judge. The other runtimes run the first step almost immediately.

3. **On Zeebe, retry the first start.** The web endpoint accepts HTTP before the broker accepts the first
   process-instance creation. The first start can time out when all components are correct. Retry the
   first start until the broker accepts it.

4. **Use a durable, shared key store for persistence claims.** The in-memory key store is in-process only.
   With it, a test that a shred survives a process restart correctly shows no persistence. This result
   tells nothing about the design. For each persistence, restart, or cross-process claim, use a durable
   store: OpenBao or RavenDB.

5. **Poll for a successful call before you start.** A control panel listens before its server is ready for
   queries. A container opens its port before the service in it answers. An open port
   does not show readiness.

6. **Stop hosts by PID or by port.** Do not `pkill -f` a pattern that also matches your own command. That
   command stops the shell or script that does the kill, during the run. The result then looks like a
   failure.

7. **Give each Durable Task workload its own task hub.** An example host and a second Durable Task
   workload on one task hub each take the activities of the other. They then fail in confusing ways.

## Read a result correctly

1. Accept only zero failures as a pass.
2. Make sure that the run covered each path that you report.

   A host or test that the run did not select is absent from the results. It certifies nothing. A
   selected host or test that cannot reach its server fails. Find the cause, and do not report it as a
   pass.
3. Match each claim to the setup.

   A persistence or restart conclusion needs a durable store. An in-memory run tells nothing about
   persistence. A cross-runtime conclusion needs the servers of those runtimes.
4. Before you report a failure, eliminate the traps above, especially traps 1 to 3.

   A failure that occurs again is a true signal. But most reports of a defect come from these setup
   traps.

The [runtime matrix](../reference/runtime-matrix.md#verifying-locally) gives the behavior of each runtime
and the note about conditional coverage.
