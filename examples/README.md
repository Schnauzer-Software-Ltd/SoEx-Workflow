> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# SoEx.Workflow examples

Runnable examples of consuming SoEx.Workflow. The baseline is PiiMaker, an IDesign Method project
(IDesign-structured business components) that every example host wires onto a runtime. Each example
then adds a thin host with the specific wiring for one runtime × consumption mode.

Each host runs as a small web control panel: it stands up the "membership" system and serves a static
page of buttons, one per external event the workflow waits for (a user verifies an account, accepts an
invite, updates payment, a leaver is offboarded). You press a button, an HTTP request hits an
auto-generated controller, the request dispatches into the SoEx manager, and the durable flow moves
forward. That lets you exercise the whole workflow interactively, by hand, instead of watching one
hardcoded script.

> Built as its own solution (`SoEx.Workflow.Examples.sln`) so the shipped library build
> (`../SoEx.Workflow.sln`) stays clean.

## Running — the web control panel

Start any host and open its page; pick the host from the in-page dropdown (it can drive any host
that's running). Start only the hosts whose backend is up.

| Host | Port | Backend needed | Flows on the panel |
|---|---|---|---|
| InProc | 5001 | none | A onboarding · B renewal · D erasure |
| Temporal | 5002 | Temporal server `:7233` (Docker) | A · B · C offboarding · D |
| DurableTask | 5003 | DTS emulator `:8080` (Docker) | A · B · C · D |
| Restate | 5004 | restate-server `:8088`/`:9070` (Docker) + cargo | A · B · C · D |
| Elsa | 5005 | none (SQLite file) | A · D · restart-host durability demo |
| Zeebe | 5006 | Camunda 8 Run `:26500`/Operate `:8090` | A onboarding (native BPMN flow; native-only) |

```
dotnet run --project examples/PiiMaker/Hosts/InProc -- 5001      # then open http://localhost:5001
```

The page capability-gates its cards from `GET /example/host`, so each host shows only the flows it can
drive. Buttons that fire the awaited events POST to the generated `IMembershipEntry` controller
(`/IMembershipEntry/<Operation>`). A few example-only endpoints (`/example/*`) provide scenario toggles
(force a billing decline to drive dunning), PII-free instance status (the per-instance key flips
live → shredded at completion), and the erasure sweep.

The dropdown and links are origin-aware (built from the address the page was loaded on), so the panels
work unchanged whether you open them on `localhost` or front them with a reverse proxy at another
address. **These panels are demo hosts with no authentication or authorization**: every endpoint,
including the erasure sweep, is open to anyone who can reach it. They are meant for `localhost`. If you
front them with a reverse proxy, put authentication on the proxy (and don't expose them publicly):
nothing in the example gates a caller. If you set `PIIMAKER_DASHBOARD_PORT` (and `PIIMAKER_DASHBOARD_SCHEME=https` where the
dashboard needs a secure context) for a runtime that has a backend dashboard (Temporal, DurableTask,
Restate), its `/example/host` response carries that, and the page shows a link to the runtime's
dashboard. Unset, no link appears.

### One-command dev harness (`examples/dev/piimaker.sh`)

To avoid starting backends by hand, `examples/dev/piimaker.sh` provisions every backend in Docker
(idempotently: rerun it freely; an existing container is restarted, never recreated) and launches one
runtime host with one key store, printing the panel URL:

```
./examples/dev/piimaker.sh --runtime temporal --keystore openbao   # provision what's needed + launch
./examples/dev/piimaker.sh                                         # interactive: pick runtime + key store
./examples/dev/piimaker.sh provision-only                          # bring up ALL backends, launch nothing
./examples/dev/piimaker.sh down                                    # stop + remove all dev containers
```

The key store (`--keystore`, or `PIIMAKER_KEYSTORE`) is the crypto-shred root and the one piece worth
making durable in order to see the behaviour:

| `--keystore` | Backing store | What it shows |
|---|---|---|
| `inmemory` (default) | in-process | the reference store; a host restart forgets every key |
| `openbao` | OpenBao Transit `:8200` (root token `root`) | the key never leaves the server; per-instance Transit key `inst-<hex>` |
| `ravendb` | RavenDB dev server `:8085` | a master-key-wrapped data key in compare-exchange `ikey/<instanceId>` |

With a durable store you can watch the whole crypto-shred lifecycle and prove it survives a restart.
Start a flow (the panel's `GET /example/status/{id}` reports `keyLive:true`, and the key appears in
OpenBao/RavenDB), stop the host with Ctrl-C (the backend container stays up), relaunch the same
`--runtime --keystore`, and the status is still `keyLive:true`, where a fresh in-memory store would
have reported `false`. Erasing (or completing) the flow then drops the key, and it stays gone across
further restarts. Pair `--keystore ravendb` with `--runtime elsa` (durable flow state in SQLite) for
the most convincing end-to-end durability demo.

Set `PIIMAKER_IDEMPOTENCY=ravendb` (alongside the RavenDB backend) to use the durable
`RavenDbIdempotencyStore` for step idempotency and the Elsa gateway's idempotent re-raise, so a
re-raise carrying the same `raiseId` stays deduped across a host restart. The default `inmemory`
dedupes within the process.

Set `PIIMAKER_SUBJECTINDEX=ravendb` (RavenDB server) or `=efcore` (a SQLite file via
`PIIMAKER_SUBJECTINDEX_SQLITE`) to use a durable subject→instance index, so right-to-erasure routing
survives a restart and is visible cross-process. The default is `inmemory`. Together with the key
store and idempotency store, this makes the whole governance trio durable.

The built-in erasure-maintenance runner (sweep abandoned + re-drive held + review deadlines) runs by
default; set `PIIMAKER_MAINTENANCE=off` to disable it. `PIIMAKER_MAINTENANCE_STORE=ravendb`/`efcore`
(with `PIIMAKER_MAINTENANCE_SQLITE` for the EF Core file) makes its held/request state durable. The
built-in runner is in-process with no leader election; for production, disable it and host a dedicated
scheduler separately that calls the utility's one-pass operations.

A few notes. The restate runtime also needs `cargo`, because its host builds the Restate sidecar (the
script does not). The zeebe runtime runs Camunda 8 + Elasticsearch (about 2 GB RAM) so the v2
REST/Operate read model exists. The RavenDB key store uses a demo-only master key unless
`PIIMAKER_RAVENDB_KEK` (base64, 32 bytes) is set. Backends bind to `127.0.0.1` only and have no
volumes, so `down` wipes their data.

### How the API is generated

The host projects reference `SoEx.Method.Generators.AspNetCore`, a source generator that lifts every
interface in a `*.Manager.*.Interface` assembly into a POST controller dispatching via
`Proxy.ForService<I>()`. `IMembershipEntry` lives in its own `PiiMaker.Manager.Membership.Interface`
assembly for this reason; a per-request middleware (`UseSoContext`) sets the SoEx container
scope so the generated controller resolves the composed manager and dispatches through the pipeline.
The generator, middleware and JSON polymorphism resolver are wired once in `PiiMaker/Hosts/Common`
(`PiiMaker.Hosting.MembershipWebHost`); the static UI lives once in `PiiMaker/Hosts/Common/wwwroot`.

## PiiMaker — the shared method project

`PiiMaker/` is the consumer's business logic, with no hosting (that lives in each example):

- One entrypoint, `IMembershipManager` / `MembershipManager`, models every flow as a distinct
  operation. A host governs the one it wants by name (`GovernedStep` operation selection). Operations
  come in two shapes: portable ops return a `WorkflowAction` (the component is the flow, and the
  generic driver drives it on any runtime), while native ops return a PII-free `StepReceipt` (the
  backend owns the flow and calls the op per step).
- It is a real SoEx System rather than a mock. The shared composition (`PiiMaker/Hosts/Common`,
  `PiiMaker.Hosting.MembershipSystem.Compose`) stands up a `Topology.System`: a "membership" subsystem
  whose entrypoint is the manager (hosted on a `WorkflowBinding` for the governed step) and whose
  components are the Engine and Access roles (`ISubscriptionEngine`, `IIdentityAccess`,
  `IBillingAccess`, `IProvisioningAccess`, `IRetainedRecordAccess`), each with Task-based contracts.
  The manager calls them as proxies through the pipeline (in-proc transport); each component's state
  is a singleton on its own host `ServiceCollection`. Every host reuses this; only the way it drives
  the governed step/termination onto a runtime differs.
- Governance is built in: the subject is `SubjectContext.Managed(email)`; results and event names are
  PII-free by construction; must-retain PII is written outward in `OnRetaining` via the Retention
  component, never returned. (`MembershipManager` implements `IErasureEvents`; the termination invokes
  it through a system-resolved proxy.)

### The flows (operations on `IMembershipManager`)

| Flow | Operation(s) | Demonstrates |
|---|---|---|
| **A Onboarding** | `Onboard` (portable), `OnboardStep` (native) | wait-for-event + timeout→compensation, idempotent assign, termination shred |
| **B Subscription** | `Renew` (portable), `RenewStep` (native) | continue-as-new across renewal periods, dunning (backoff + payment-updated wait), cancel |
| **C Offboarding** | `OffboardStep` (native-only) | parallel revocation fan-out, archive-in-`OnRetaining`, quarantine on archive failure |
| **D Erasure** | (no op — `IErasureEvents` + `ErasureCoordinator`) | "forget subject S" sweep over A/B/C instances |

### Triggering from outside (`IMembershipEntry`)

Production systems don't drive a workflow from the code that started it: an identity provider's webhook
says "this account was verified", a payment processor says "this card was updated". These callers have
no instance handle, no payload, and no flow knowledge. The manager's second contract,
`IMembershipEntry`, is that seam, and every host drives its demos through it.

To start, `StartOnboarding(org, email, offer)` / `StartRenewal(subscriberId)` /
`StartOffboarding(subjectId)` derive the PII-free instance id from business identity
(`DeterministicInstanceId`: a hash, so an email never appears in a journaled id), seal the seed
(`WorkflowSealer`, the seal side only; the component never holds the dispatch endpoint), and submit
it through the engine-agnostic `IWorkflowGateway` the host wired.

To continue, `AccountVerified` / `InviteAccepted` / `PaymentUpdated` re-derive the same id from the
same business identity and raise a bare event with no payload. Each portable wait pre-sealed its own
`OnEvent` continuation into the journal, so the flow decides what the event means; an event raised
with a payload still carries the next step, so data-carrying events keep working. (Offboarding is a
self-completing fan-out, so it has no continuation events.)

`IMembershipEntry` is also what the AspNetCore method generator lifts into the panel's HTTP API; it
lives in its own `PiiMaker.Manager.Membership.Interface` assembly so the generator picks it up. The UI
buttons are those operations; the only runtime-specific code remains the host's seam wiring below
them.

That seam wiring is one `IWorkflowGateway` (+`WorkflowSealer`) per flow: `InProcWorkflowGateway`,
`TemporalWorkflowGateway`, `DurableTaskWorkflowGateway` (portable flow or a native orchestration via
its input factory), `ElsaWorkflowGateway` (start + resume by correlation id, durable across a host
restart), `RestateWorkflowGateway` (HTTP ingress; the bare event crosses the language boundary into
the Restate sidecar). The entry-driven demo code above the seam is identical on every host.

## Hosts

Each host is the consumer composition root for one cell. It wires `MembershipManager`
(operation-by-name) onto a runtime × mode and serves the control panel (`dotnet run --project
examples/PiiMaker/Hosts/<host> -- <port>`). All six are shipped and runnable.

- [`PiiMaker/Hosts/InProc`](PiiMaker/Hosts/InProc) (`:5001`) — InProc, portable flow: onboarding (A),
  subscription renewal (B) and the erasure sweep (D). No backend or Docker. Offboarding (C) is
  native-only, so it isn't runnable on the portable flow and its card is hidden.
- [`PiiMaker/Hosts/Elsa`](PiiMaker/Hosts/Elsa) (`:5005`) — onboarding (A) on Elsa, native flow,
  durable via EF Core SQLite. The durability demo is interactive: start onboarding (it parks on the
  invite-accepted bookmark, persisted to SQLite), press "Restart host" (disposes the provider and
  rebuilds a fresh one over the same database), then deliver invite-accepted: the saga resumes on the
  new process and shreds. No Docker; SQLite is a file.
- [`PiiMaker/Hosts/Temporal`](PiiMaker/Hosts/Temporal) (`:5002`) — all flows on a Temporal server
  (`localhost:7233`, Docker required): onboarding (A, portable), subscription renewal (B, portable
  continue-as-new), offboarding (C, native fan-out) and the erasure sweep (D). Native offboarding is
  started through a small `IWorkflowGateway` over the consumer's `NativeOffboardWorkflow`; the generic
  gateway would start the portable flow.
- [`PiiMaker/Hosts/DurableTask`](PiiMaker/Hosts/DurableTask) (`:5003`) — all flows on the modern
  Durable Task SDK against a Durable Task Scheduler (`localhost:8080`; the DTS emulator in Docker):
  onboarding (A, native), subscription renewal (B, portable continue-as-new on the scheduler),
  offboarding (C, native fan-out) and the erasure sweep (D). One task hub, so the two modes coexist by
  orchestration name: the portable flow (B) and the consumer-authored native orchestrations (A, C) run
  side by side.
- [`PiiMaker/Hosts/Restate`](PiiMaker/Hosts/Restate) (`:5004`) — the cross-language runtime. Restate
  ships no .NET SDK, so the durable flow runs out-of-process in a compiled Restate sidecar hosted by
  restate-server, calling back to a thin .NET governed-step host over HTTP. This example ships its own
  sidecar ([`sidecar-rs/`](PiiMaker/Hosts/Restate/sidecar-rs), built and spawned by the host), separate
  from the library/test sidecar. The panel drives all flows over the language boundary: onboarding (A,
  the consumer-authored `MembershipOnboard` flow) and offboarding (C, the `MembershipOffboard`
  parallel fan-out) calling one long-lived `/gov-step` host (routed by instance-id prefix), and
  subscription renewal (B, the generic `MembershipPortable` flow with continue-as-new + dunning)
  calling its own `/step`+`/terminate` host on a separate port (the sidecar's `PORTABLE_STEP_URL`
  points there, so the two callback hosts coexist), plus the erasure sweep (D). Requires
  restate-server (`:8088`/`:9070`, Docker) and cargo to build the sidecar.
- [`PiiMaker/Hosts/Zeebe`](PiiMaker/Hosts/Zeebe) (`:5006`) — onboarding (A) on Camunda 8 / Zeebe as a
  native BPMN flow (the flow is `bpmn/membership-onboard.bpmn`, deployed to the broker at startup;
  native-only, no portable flow). A governed service-task job runs each `OnboardStep`; a process end
  execution-listener runs the crypto-shred termination. Requires Camunda 8 Run (gateway `:26500`,
  Operate `:8090`).

## Coverage matrix

Each example host wires `MembershipManager` (operation-by-name) onto one cell:

| Flow | Modes | Runtimes (on the control panels) |
|---|---|---|
| A Onboarding | native + portable | InProc (portable) · Temporal (portable) · DurableTask (native) · Elsa (native) · Restate (native) · Zeebe (native) |
| B Subscription | portable | InProc · Temporal · DurableTask · Restate |
| C Offboarding | native only | Temporal · DurableTask · Restate |
| D Erasure | operation (mode-agnostic) | every host: runs over in-flight instances; the termination shred is each runtime's hook |

All six runtime columns have a runnable web control panel (Zeebe is native-only, onboarding); the
ports and run instructions are above.
