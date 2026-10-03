> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# SoEx.Workflow.Runtime.Restate

This package is the Restate runtime adapter. Restate has no .NET SDK. Thus on this runtime, the flow runs
out-of-process in the Restate sidecar (`restate-sidecar-rs`). The sidecar is a Rust binary, and it calls
back into .NET over HTTP. The sidecar (`restate-sidecar-rs/`) is one static binary, built on
`restate-sdk`. It serves the two consumption models as two Restate services on one endpoint. You choose
one model for each instance. An instance cannot change to the other model.

## Native flow

A native flow is Rust code that the consumer writes as a Restate service in a sidecar. .NET governs each
step and the termination. The framework sidecar contains one fixed native onboarding flow,
`NativeOnboardWorkflow`, with the handlers `run` and `raise_event`. The Tier-2 tests use it. For your
own native flow, build your own sidecar. The PiiMaker example does this in
`examples/PiiMaker/Hosts/Restate/sidecar-rs`. A native flow has this shape:

- Each step is a `/gov-step` call inside `ctx.run`.
- The wait is a durable promise. `raise_event` resolves it.
- The termination is a final `/gov-terminate` call.

The .NET host for `/gov-step` and `/gov-terminate` is consumer code. This package does not ship it. The
Tier-2 test `RestateNativeStepTests` builds one inline as the worked example.

| Flow step | Restate primitive | .NET callback |
|---|---|---|
| governed step | `ctx.run` (durable, journalled) | `POST /gov-step` → the pipeline + key/subject/idempotency, returns a business result |
| wait-for-event | a durable promise resolved by `raise_event` | — |
| termination | a final `ctx.run` | `POST /gov-terminate` → crypto-shred + index prune |

## Portable flow

The portable flow is `OnboardWorkflow`, with the handlers `run` and `raise_event`. It is a generic
handler, the portable driver, in the sidecar. The handler owns the step loop. The .NET component returns
a `WorkflowAction`, flattened to an `ActionDto`. The handler maps this action onto the durable primitives
of Restate: `ctx.run`, `ctx.sleep`, `ctx.promise`, and continue-as-new.

The .NET half is `RestateWorkflowHost`, and this package ships it. It is a Kestrel host with two
endpoints:

- `POST /step` runs one step and returns the flattened action.
- `POST /terminate` runs the erasure lifecycle.

| Action | Restate primitive |
|---|---|
| step | `ctx.run` (durable, journalled) |
| delay | `ctx.sleep` (durable timer) |
| wait-for-event | a durable promise; with a timeout, raced against `ctx.sleep` → on-timeout step |
| loop (continue-as-new) | a fresh execution (`~<gen>` id suffix), carrying the logical instance id + key |
| complete | a final `ctx.run` to `/terminate` |

## Payloads and keys

Payloads are opaque base64 from end to end. The sidecar never reads them. In the portable flow,
`RestateWorkflowHost` seals these bytes with the per-instance key. Thus Restate journals ciphertext, and
the termination shred makes it unrecoverable.

The instance keys never go into restate-server or the Restate sidecar. They are only in the
`IInstanceKeyStore` of the .NET callback host (`/step` / `/gov-step`). This host seals the data before it
sends it out, and it shreds the key at the termination. Restate sees only ciphertext. Thus crypto-shred
has the same durability as that key store.

The bundled `InMemoryInstanceKeyStore` is in-process only. Some deployments have more than one process, or must
continue after a restart. These deployments must give the .NET callback host a durable, shared store.
Use one of these stores:

- the bundled `OpenBaoInstanceKeyStore`
- the bundled `RavenDbInstanceKeyStore`
- your own `IInstanceKeyStore` (DB/KMS/HSM)

## Waits, branches, and event data

The flattened `ActionDto` of a `wait` contains the sealed continuations of the wait as base64 fields. The
flow seals them before the wait:

- `onTimeout` is the step that the flow resumes into when the timer wins.
- `onEvent`, one for each branch, is the step that the flow resumes into when `raise_event` delivers a
  payload at the name of that branch. Thus an external caller can raise "this happened" with no knowledge
  of the flow.

If a branch has an `onEvent` step, that step is always the next step. A non-empty raised payload goes
with it as *event data*. Event data is a separate opaque field. The next `/step` call carries it, and only
that step reads it. If a branch has no `onEvent` step, the flow resumes directly into the raised payload.
When one of these fields has no value, it is the empty string. It is never JSON `null`.

A wait contains a `branches` array. Each entry is an `{eventName, onEvent}` pair, in the order that the
flow declared them. The sidecar parks one durable promise for each branch and races them against the
timer of the wait. The flow resumes into the continuation of the branch that resolves first. If several
branches are already resolved, the first declared branch wins.

The top-level `eventName`/`onEvent` fields repeat branch 0. This gives two results:

- If you roll the deployment back, a single-branch wait that this build journaled still replays.
- A wait journaled before branches existed reads back as a one-branch wait.

## Write-once promises

On Restate, a durable promise is write-once for each event name, for the life of a generation. Plan for
this behavior, because the other runtimes are different here. If a caller raises a branch more than one
time, Restate delivers only the first raise. A resend button is an example. If a flow needs a branch that
it can raise again, the flow must take a `Loop` after it handles the branch. The `Loop` starts a new
generation with new promises.

## Wire contract

After you change either side of this wire contract, do these steps:

1. Increase `RestateWorkflowHost.WireVersion`.
2. Increase the `WIRE_VERSION` of the sidecar to the same value.
3. Rebuild the sidecar explicitly (`cargo build --release`).

**Version handshake.** The sidecar sends its `WIRE_VERSION` in the `x-soex-wire-version` header on each
`/step` and `/terminate` call. If the version is different, the .NET host refuses the call with `400`.
Thus an old sidecar binary fails clearly. It cannot run an old contract without an error. Keep the two
constants equal. The handshake applies only to `RestateWorkflowHost` (`/step` and `/terminate`). The
`/gov-step` and `/gov-terminate` calls of a native flow carry no version header. Your own `/gov-*` host
sets that contract.

**Wire version 3** added `StepRequest.eventData`. This field is the raised payload when a wait resumes
into the `onEvent` step of a branch. It goes separately, and the `onEvent` step stays the next step. The
field is always on the wire. When a step has no event data, the field is the empty string. It is never
absent and never `null`. Event data gets to the one `/step` call after the raise, and the sidecar then
clears it. Event data does not continue through the continue-as-new of a `Loop`. The on-timeout path
never carries event data.

**Timeouts and supervision.** Each callback from the sidecar to the host has a request timeout
(`STEP_TIMEOUT_SECS`, default 60s). If the host stops responding, the call fails, and the retry of Restate
runs it again. Thus a host that stops responding does not block the invocation. The sidecar is a long-lived process that you build in-tree.
Run it under a restart policy (`systemd`, a container `restart:` policy), so that the system recovers from
a crash. Rebuild it each time `WireVersion` changes.

## Gateway

`RestateWorkflowGateway` is the `IWorkflowGateway` over the ingress HTTP API for a sidecar service. This
package ships it.

- `StartAsync` submits `run` and does not wait for a result (`/send`).
- `RaiseEventAsync` posts `raise_event`. If you do not give a payload, it sends the empty string. The
  on-event rule above then applies.

A re-raise is idempotent by design. The sidecar resolves a durable promise with the event name as its
key. The promise is write-once, so a second raise of one name has no effect. The handler reads the promise
before it resolves it. `RaiseEventAsync` accepts a `raiseId`, but here it is advisory only, because the
write-once promise already gives this protection. Thus Restate cannot deliver two different raises of one
name. This is the latch model of Restate.

## Authentication and transport security

A shared bearer token authenticates the connection from the sidecar to .NET. The sidecar sends
`Authorization: Bearer $STEP_TOKEN` on each `/step`, `/terminate`, `/gov-step`, and `/gov-terminate`
call. `RestateWorkflowHost.Build(stepUrl, step, termination, authToken)` refuses all other calls with
401. It compares the token in constant time. A native `/gov-*` host must do the same.

> [!CAUTION]
> Do not make the step endpoints reachable without authentication. These endpoints run governed steps and
> crypto-shred.

The sidecar requires `STEP_TOKEN`. By default, the sidecar binds to loopback. To expose it, set `BIND`
deliberately.

This token protects only the connection from the **sidecar to .NET**. The connection from your app to the
Restate **ingress** has **no framework authentication**. `RestateWorkflowGateway` POSTs the start and the
raise on this connection. The optional `IGatewayAuthorizer` is an allow/deny hook at the application
level. It is not a transport credential. The data on this connection is the sealed seed (ciphertext) and
the instance id, which contains no PII. Thus the exposure is low. You are responsible for authentication
and TLS on the ingress, with Restate ingress authentication or a network boundary. You are also
responsible for the security of the step-host connection off loopback.

> [!WARNING]
> Keep the step-host connection on **loopback**, or protect it with TLS. On plain HTTP, the bearer token
> goes in **clear text**. The step payloads are sealed ciphertext. The token has no seal. On an
> `http://` `STEP_URL`/`BIND`, all persons who can see the traffic can see the token.

To use TLS when the connection must cross a network, do these steps:

1. Give the sidecar an `https://` `STEP_URL`. The sidecar verifies TLS against the system/web-PKI roots.
2. To also trust a private or internal CA, set `STEP_CA_CERT=/path/to/ca.pem`. A bad path stops the
   sidecar at startup. The sidecar never falls back to plaintext.
3. Serve the .NET step host over HTTPS. Give an `https://` `stepUrl` to `RestateWorkflowHost.Build`.
4. Give the server certificate in the optional `serverCertificate` parameter. Alternatively, use the
   standard ASP.NET Core Kestrel configuration (`Kestrel:Certificates:Default` /
   `ASPNETCORE_Kestrel__Certificates__Default__Path`).

## Run locally (Tier-2)

```sh
# 1. restate-server (ingress on :8088 to avoid the DTS emulator's :8080/:8081)
docker run -d --name restate --network host \
  -e RESTATE_INGRESS__BIND_ADDRESS=0.0.0.0:8088 \
  ghcr.io/restatedev/restate:latest

# 2. the Restate sidecar (serves both OnboardWorkflow + NativeOnboardWorkflow) — STEP_URL is the .NET host,
#    STEP_TOKEN the shared secret it presents (the .NET host must be built with the same token)
cd restate-sidecar-rs && cargo build --release
STEP_URL=http://127.0.0.1:9090 BIND=127.0.0.1:9080 STEP_TOKEN=dev-secret ./target/release/restate-sidecar
```

Two Tier-2 tests in the private test repository use the sidecar. Each test registers the sidecar
deployment itself (`POST :9070/deployments`, `force`). Each test hosts its .NET half in-process on :9090:

- One native test posts to `/NativeOnboardWorkflow/...` against a `/gov-*` host.
- One portable conformance test posts to `/OnboardWorkflow/...` against `RestateWorkflowHost`.
