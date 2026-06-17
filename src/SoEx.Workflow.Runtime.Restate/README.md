> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# SoEx.Workflow.Runtime.Restate

The Restate runtime adapter. Restate ships no .NET SDK, so on this runtime the flow runs out-of-process
in the Restate sidecar (`restate-sidecar-rs`), a Rust binary, and calls back into .NET over HTTP. The one
binary (`restate-sidecar-rs/`, built on `restate-sdk`, a single static binary) serves both consumption
models as two Restate services on one endpoint. You pick one per instance; there is no migration
between them.

**Native flow:** `NativeOnboardWorkflow` (handlers `run` + `raise_event`). The consumer authors the
flow here, in the sidecar, and .NET only governs each step and the termination. Each step is a
`/gov-step` call inside `ctx.run`; the wait is a durable promise resolved by `raise_event`; the
termination is a final `/gov-terminate` call. The .NET `/gov-step`+`/gov-terminate` host is consumer
code (not shipped here); the Tier-2 `RestateNativeStepTests` builds one inline as the worked example.

| Flow step | Restate primitive | .NET callback |
|---|---|---|
| governed step | `ctx.run` (durable, journalled) | `POST /gov-step` → the pipeline + key/subject/idempotency, returns a business result |
| wait-for-event | a durable promise resolved by `raise_event` | — |
| termination | a final `ctx.run` | `POST /gov-terminate` → crypto-shred + index prune |

**Portable flow:** `OnboardWorkflow` (handlers `run` + `raise_event`). A generic handler (the portable
driver, living in the sidecar) that owns the step loop and maps the .NET component's returned
`WorkflowAction` (flattened to an `ActionDto`) onto Restate's durable primitives (`ctx.run` /
`ctx.sleep` / `ctx.promise` / continue-as-new). The .NET half is `RestateWorkflowHost`, which is
shipped here: a Kestrel host exposing `POST /step` (run one step, return the flattened action) and
`POST /terminate` (the erasure lifecycle).

| Action | Restate primitive |
|---|---|
| step | `ctx.run` (durable, journalled) |
| delay | `ctx.sleep` (durable timer) |
| wait-for-event | a durable promise; with a timeout, raced against `ctx.sleep` → on-timeout step |
| loop (continue-as-new) | a fresh execution (`~<gen>` id suffix), carrying the logical instance id + key |
| complete | a final `ctx.run` to `/terminate` |

Payloads are opaque base64 end to end; the sidecar never interprets them. For the portable flow those
bytes are sealed under the per-instance key by `RestateWorkflowHost`, so what Restate journals is
ciphertext, and the termination shred makes it unrecoverable.

**Where the keys live.** The instance keys never enter restate-server or the Restate sidecar. They
live only in the `IInstanceKeyStore` of the .NET callback host (`/step` / `/gov-step`), which seals on
the way out and shreds at the termination; Restate sees ciphertext only. Crypto-shred is therefore
exactly as durable as that key store. The bundled `InMemoryInstanceKeyStore` is in-process only, so a
multi-process or restart-surviving deployment must back the .NET callback host with a durable, shared
store: the bundled `OpenBaoInstanceKeyStore` or `RavenDbInstanceKeyStore`, or your own
`IInstanceKeyStore` (DB/KMS/HSM).

The flattened `ActionDto` a `wait` returns carries two pre-sealed continuations as base64 fields:
`onTimeout` (the step to resume into when the timer wins) and `onEvent` (the step to resume into when
`raise_event` delivers an empty payload, so an external caller can raise "this happened" with no flow
knowledge). A non-empty raised payload always wins and becomes the next step. Both fields use the empty
string, never JSON `null`, when absent. After changing either side of this wire contract, rebuild the
Restate sidecar explicitly (`cargo build --release`); a stale binary silently runs the old contract.

`RestateWorkflowGateway` (shipped here) is the `IWorkflowGateway` over the ingress HTTP API for a
sidecar service. `StartAsync` submits `run` fire-and-forget (`/send`); `RaiseEventAsync` posts
`raise_event`, where an omitted payload sends the empty string and triggers the on-event rule above. A
re-raise is idempotent by construction: the sidecar resolves a durable promise keyed by the event name,
which is write-once, so a second raise of one name is a no-op (the handler peeks before resolving).
`RaiseEventAsync` accepts a `raiseId`, but it is advisory here, since the write-once promise already
subsumes it. A consequence is that Restate cannot deliver two distinct raises of one name; that is its
latch model.

## Authentication and transport security

The sidecar-to-.NET hop is authenticated with a shared bearer token. The sidecar sends
`Authorization: Bearer $STEP_TOKEN` on every `/step`, `/terminate`, `/gov-step`, and `/gov-terminate`
call, and `RestateWorkflowHost.Build(stepUrl, step, termination, authToken)` rejects anything else with
401 (a native `/gov-*` host should do the same; the token is compared in constant time). The endpoints run
governed steps and crypto-shred, so they must never be reachable unauthenticated. `STEP_TOKEN` is required
by the sidecar, which binds loopback by default (set `BIND` to expose it deliberately).

This token protects only the **sidecar → .NET** hop. The other direction — your app → the Restate
**ingress** (where `RestateWorkflowGateway` POSTs the start/raise) — carries **no framework auth**; the
optional `IGatewayAuthorizer` is an application-level allow/deny hook, not a transport credential. What
crosses there is the sealed seed (ciphertext) and the PII-free instance id, so exposure is low, but
authenticating and TLS-protecting the ingress is the consumer's responsibility (Restate ingress auth /
a network boundary), the same way the step-host seam above is yours to secure off loopback.

**The token rides this hop in the clear over plain HTTP.** The step payloads are sealed ciphertext, but the
bearer token is not — so on `http://` `STEP_URL`/`BIND` the token is exposed to anyone who can see the
traffic. Keep the seam on **loopback** (the default), or secure it with TLS when it must cross a network:

- Point the sidecar at an `https://` `STEP_URL`. TLS is verified against the system/web-PKI roots; set
  `STEP_CA_CERT=/path/to/ca.pem` to additionally trust a private/internal CA (a bad path is fatal at
  startup, never a silent fallback to plaintext).
- Serve the .NET step host over HTTPS: pass an `https://` `stepUrl` to `RestateWorkflowHost.Build` and
  supply the server certificate via the optional `serverCertificate` parameter, or via the standard ASP.NET
  Core Kestrel config (`Kestrel:Certificates:Default` / `ASPNETCORE_Kestrel__Certificates__Default__Path`).

## Running locally (Tier-2)

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

Two Tier-2 tests in the private test repo exercise it, each self-registering the sidecar deployment
(`POST :9070/deployments`, `force`) and hosting its .NET half in-process on :9090: one native (posts to
`/NativeOnboardWorkflow/...` against a `/gov-*` host) and one portable conformance test (posts to
`/OnboardWorkflow/...` against `RestateWorkflowHost`).
