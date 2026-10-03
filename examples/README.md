> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# SoEx.Workflow examples

These examples show how to consume SoEx.Workflow. You can run each of them. The base of the examples is
PiiMaker, an IDesign Method project. PiiMaker holds the business components, in the IDesign structure.
Each example host connects PiiMaker to a runtime. A host is a small project that holds the wiring for one
runtime and one consumption model.

Each host is a small web control panel. The host starts the "membership" system and serves a static page
of buttons. Each button is one external event that the workflow waits for:

- a user verifies an account
- a user accepts an invite
- a user updates the payment details
- the system offboards a leaver

When you push a button, an HTTP request goes to the trigger controller of the Manager. The controller
dispatches the request into the SoEx Manager, and the durable flow moves forward. Thus you can operate the
full workflow by hand, one event at a time.

> The examples have their own solution (`SoEx.Workflow.Examples.sln`). Thus the build of the shipped
> library (`../SoEx.Workflow.sln`) contains only the library.

## Other examples

- [**Statechart**](Statechart/README.md): an expense-approval Manager. Its process is a statechart that
  you draw in a tool and export as XState v6 JSON. The component layout is the same as in PiiMaker. All
  the business logic is under `Component/`, and all other code is framework. It is a console program and
  needs no runtime server: `dotnet run --project examples/Statechart`.
- **MultiManager**: two business Managers on one workflow utility. Right-to-erasure goes to the Manager
  that owns each instance. It is a console program and needs no runtime server:
  `dotnet run --project examples/MultiManager`.

## Run the web control panel

1. Start the hosts whose backend is up. Start no other hosts.
2. Open the page of one host.
3. Select a host in the dropdown on the page.

The page opens with the host that served it selected. The dropdown lists InProc, Temporal,
DurableTask, Restate, Elsa, and Zeebe. The page can operate each of these hosts that runs.

| Host | Port | Backend needed | Flows on the panel |
|---|---|---|---|
| InProc | 5001 | none | A onboarding · B renewal · D erasure |
| Temporal | 5002 | Temporal server `:7233` (Docker) | A · B · C offboarding · D |
| DurableTask | 5003 | DTS emulator `:8080` (Docker) | A · B · C · D |
| Restate | 5004 | restate-server `:8088`/`:9070` (Docker) + cargo | A · B · C · D |
| Elsa | 5005 | none (SQLite file) | A · D · restart-host durability demo |
| Zeebe | 5006 | Camunda 8 Run `:26500`/Operate `:8090` | A onboarding (native BPMN flow; native-only) · D |

```
dotnet run --project examples/PiiMaker/Hosts/InProc -- 5001      # then open http://localhost:5001
```

The page reads the capabilities of the host from `GET /example/host`. It shows a card for each flow. The
cards for the flows that the host can operate are enabled. The other cards are disabled and show a note.
The buttons that send the awaited events POST to the trigger controller of the
Manager (`/IMembershipManager/<Operation>`). Some endpoints exist only for the examples (`/example/*`).
They give these functions:

- scenario switches, for example a forced billing decline that starts dunning
- the status of an instance, with no PII: the per-instance key changes from live to shredded at completion
- the erasure sweep

The page builds the dropdown and the links from the address that it loaded from. Thus the panels work with
no change on `localhost` and behind a reverse proxy at a different address.

> [!WARNING]
> Keep the panels on `localhost`. Do not expose them publicly. The panels are demo hosts with no
> authentication and no authorization. Each endpoint, the erasure sweep included, is open to all callers
> that can reach it. If you put a reverse proxy in front of the panels, put authentication on the proxy.
> The example itself does no check on a caller.

Temporal, DurableTask, Restate, and Zeebe each have a dashboard for the runtime. For these runtimes, you can set
`PIIMAKER_DASHBOARD_PORT`. If the dashboard needs a secure context, also set
`PIIMAKER_DASHBOARD_SCHEME=https`. The `/example/host` response then contains these values, and the page
shows a link to the dashboard of the runtime. If you do not set them, the page shows no link.

### One-command dev harness (`examples/dev/piimaker.sh`)

The script `examples/dev/piimaker.sh` provisions each backend in Docker. It then starts one runtime host
with one key store and prints the URL of the panel. The script is idempotent, so you can run it again at
any time. It restarts a container that exists. It does not create that container again.

```
./examples/dev/piimaker.sh --runtime temporal --keystore openbao   # provision what's needed + launch
./examples/dev/piimaker.sh                                         # interactive: pick runtime + key store
./examples/dev/piimaker.sh provision-only                          # bring up ALL backends, launch nothing
./examples/dev/piimaker.sh down                                    # stop + remove all dev containers
```

The key store (`--keystore`, or `PIIMAKER_KEYSTORE`) is the root of crypto-shred. To see the crypto-shred
behavior across a restart, use a durable key store.

| `--keystore` | Backing store | What it shows |
|---|---|---|
| `inmemory` (default) | in-process | the reference store; a host restart forgets every key |
| `openbao` | OpenBao Transit `:8200` (root token `root`) | the key never leaves the server; per-instance Transit key `inst-<hex>` |
| `ravendb` | RavenDB dev server `:8085` | a master-key-wrapped data key in compare-exchange `ikey/<instanceId>` |

With a durable key store, you can see the full crypto-shred lifecycle and see that it continues after a
restart:

1. Start a flow. The panel's `GET /example/status/{id}` reports `keyLive:true`, and the key is in OpenBao or RavenDB.
2. Stop the host with Ctrl-C. The backend container stays up.
3. Start the host again with the same `--runtime --keystore`.
4. Look at the status again. It is still `keyLive:true`. A new in-memory store reports `false`.
5. Erase or complete the flow. The key store removes the key, and the key stays removed after more restarts.

Use a runtime that keeps the flow state outside the host process: Temporal, DurableTask, Restate, or
Zeebe. The Elsa host makes a new SQLite file at each start of the process, and deletes it when the process
stops. Thus an Elsa flow does not continue after Ctrl-C. To see Elsa durability, use the **Restart host**
button on its panel.

To use the durable `RavenDbIdempotencyStore`, set `PIIMAKER_IDEMPOTENCY=ravendb` and run the RavenDB
backend. This store then holds the step idempotency and the idempotent re-raise of the Elsa gateway. A
re-raise with the same `raiseId` then stays deduplicated across a host restart. The default `inmemory`
store deduplicates in one process only.

To use a durable subject index, set `PIIMAKER_SUBJECTINDEX=ravendb` (RavenDB server) or `=efcore` (a
SQLite file in `PIIMAKER_SUBJECTINDEX_SQLITE`). With a durable subject index, right-to-erasure routing
continues after a restart, and other processes can see the index. The default is `inmemory`. The key store,
the idempotency store, and the subject index together are the governance trio. With these three settings,
all three are durable.

The built-in erasure maintenance runner runs by default. It sweeps abandoned instances, re-drives held
instances, and reviews deadlines. To disable it, set `PIIMAKER_MAINTENANCE=off`. To make its held state and
its request state durable, set `PIIMAKER_MAINTENANCE_STORE=ravendb` or `efcore`. For EF Core, set the file
in `PIIMAKER_MAINTENANCE_SQLITE`.

If `PIIMAKER_SUBJECTINDEX` and `PIIMAKER_MAINTENANCE_STORE` have the same durable value, one physical store
holds the subject index and the maintenance state. For `efcore`, this store is the SQLite file in
`PIIMAKER_ERASURE_SQLITE` (default `/tmp/piimaker-erasure.db`). The host then ignores
`PIIMAKER_SUBJECTINDEX_SQLITE` and `PIIMAKER_MAINTENANCE_SQLITE`. For `ravendb`, this store is the database
in `PIIMAKER_ERASURE_DATABASE` (default `PiiMakerErasure`).

The built-in runner runs in-process and has no leader election. For
production, disable it. Host a dedicated scheduler as a separate process that calls the one-pass operations
of the utility.

More facts about the script:

- The restate runtime also needs `cargo`. Its host builds the Restate sidecar. The script does not build it.
- The zeebe runtime runs Camunda 8 and Elasticsearch, and needs approximately 2 GB of RAM. Elasticsearch
  supplies the v2 REST/Operate read model.
- The RavenDB key store uses a master key for demos only. To supply a master key, set
  `PIIMAKER_RAVENDB_KEK` (base64, 32 bytes).
- The backends bind to `127.0.0.1` only and have no volumes. Thus `down` deletes their data.

### How the host exposes the trigger API

The HTTP API of the panel is the inbound trigger seam (`IMembershipManager`) as a POST controller. The
controller dispatches through `Proxy.ForService<I>()`. In this example, the controller is hand-written:
`PiiMaker.Hosting.IMembershipManagerController`, which serves `/IMembershipManager/<Operation>`. A
per-request middleware (`UseSoContext`) sets the scope of the SoEx container. The controller can then
resolve the composed Manager and dispatch through the pipeline.

`PiiMaker/Hosts/Common` (`PiiMaker.Hosting.MembershipWebHost`) wires these parts one time for all hosts:

- the controller
- the middleware
- the resolver for JSON polymorphism

The static UI is in `PiiMaker/Hosts/Common/wwwroot`, one copy for all hosts.

> [!NOTE]
> The source generator `SoEx.Method.Generators.AspNetCore` writes this same controller. It makes a POST
> controller for each interface in a `*.Manager.*.Interface` assembly, with the interface name as the key.
> This example does not use the generator. The Manager here has three interfaces with the same name: the
> trigger seam `IMembershipManager`, and the governed-step contracts `Native.IMembershipManager` and
> `Portable.IMembershipManager`. Controllers with the same name collide. Usually a trigger seam is a single
> interface, and no contract in a sub-namespace has the same name. In that case, add the generator package
> to the hosting project and delete the hand-written controller. The generated output is identical.

## PiiMaker: the shared Method project

`PiiMaker/` holds the business logic of the consumer. It contains no hosting code. Each example holds its
own hosting code.

- One entrypoint component, `MembershipManager`, has a separate operation for each flow. The operations are
  in three contracts:
  - The inbound trigger seam: `IMembershipManager`.
  - The portable operations: `Portable.IMembershipManager`. Each returns a `WorkflowAction`. The component
    is the flow, and the generic driver runs it on each runtime.
  - The native single-step operations: `Native.IMembershipManager`. Each returns a `StepReceipt` with no
    PII. The runtime owns the flow and calls the operation for each step.

  A host selects the operation to govern by its name (`GovernedStep` operation selection).

  > [!NOTE]
  > In this demo, `MembershipManager` is a partial class across the `*.Native` and `*.Portable`
  > sub-namespaces. The operations of each model are explicit interface implementations. This split makes
  > the demo easier to read: you can read the native shape and the portable shape of each flow separately.
  > The pattern is optional. A real consumer can put all operations on one interface in one class, or
  > divide them in a different way. SoEx governs an operation by its name. The contract, namespace, or file
  > of the operation has no effect.
- PiiMaker is a real SoEx System. The shared composition (`PiiMaker/Hosts/Common`,
  `PiiMaker.Hosting.MembershipSystem.Compose`) makes a `Topology.System`. The system has one subsystem,
  "membership":
  - Its entrypoint is the Manager. A `WorkflowBinding` hosts the Manager for the governed step.
  - Its components are the Engine and the ResourceAccess roles: `ISubscriptionEngine`, `IIdentityAccess`,
    `IBillingAccess`, `IProvisioningAccess`, `IRetainedRecordAccess`. Each has Task-based contracts.

  The Manager calls these components as proxies through the pipeline, on the in-proc transport. The state
  of each component is a singleton on the `ServiceCollection` of its own host. All hosts use this
  composition. Each host differs only in how it puts the governed step and the governed termination on a
  runtime.
- The example includes governance:
  - The subject is `SubjectContext.Managed(email)`.
  - The results and the event names contain no PII, by design.
  - PII that the system must retain goes out through the Retention component in `OnRetaining`. The Manager
    never returns it.
  - `MembershipManager` implements `IErasureEvent`. The termination calls it through a proxy that the
    system resolves.

### The flows (the governed-step operations)

The portable operations are on `Portable.IMembershipManager`. The native single-step operations are on
`Native.IMembershipManager`. A host selects the operation to govern by its name.

| Flow | Operation(s) | Demonstrates |
|---|---|---|
| **A Onboarding** | `Onboard` (portable + native) | wait-for-event + timeout→compensation, idempotent assign, termination shred |
| **B Subscription** | `Renew` (portable + native; the hosts run only the portable operation) | continue-as-new across renewal periods, dunning (backoff + payment-updated wait), cancel |
| **C Offboarding** | `Offboard` (native-only) | parallel revocation fan-out, archive-in-`OnRetaining`, quarantine on archive failure |
| **D Erasure** | (no op: `IErasureEvent` + `ErasureCoordinator`) | "forget subject S" sweep over A/B/C instances |

### Trigger a flow from outside (`IMembershipManager`)

In a production system, external callers move a workflow forward. For example, the webhook of an identity
provider reports "this account is verified". A payment processor reports "this card is updated". These
callers have no instance handle, no payload, and no knowledge of the flow.

`IMembershipManager` is the inbound trigger contract of the Manager. It has one operation,
`Trigger(TriggerBase trigger)`. `TriggerBase` is a closed set with one case for each trigger. Each case
contains only business identity. Each host operates its demos through this contract.

To start a flow, use the case `TriggerBase.StartOnboarding(OrgId, Email, Offer)`,
`StartRenewal(SubscriberId)`, or `StartOffboarding(SubjectId)`. Each start case does these steps:

1. It derives the instance id from business identity with `DeterministicInstanceId.Keyed`. The instance id
   contains no PII.
2. It seals the seed with `WorkflowSealer`. `WorkflowSealer` is the seal side only. The component never
   holds the dispatch endpoint.
3. It submits the seed through the `IWorkflowGateway` that the host wired. This gateway works with each
   runtime.

`DeterministicInstanceId.Keyed` is an HMAC under a deployment secret. Thus an email never appears in an
instance id in the journal. A party that knows the identity but not the secret cannot derive or confirm
the instance id.

To continue a flow, use the case `TriggerBase.AccountVerified`, `InviteAccepted`, or `PaymentUpdated`.
Each continue case derives the same instance id again from the same business identity. It then raises an
event with no payload. Each branch of a portable wait sealed its own `OnEvent` continuation into the
journal before the wait. Thus the flow decides what the event means. If a wait names several events, it
resumes into the continuation of the branch that the caller raised. An event raised with a payload also
continues to the next step. Thus events that carry data continue to work. Offboarding completes by itself after a
fan-out, so it has no continuation events. `Trigger` returns the derived instance id in each case.

The renewal flow shows a wait with more than one branch. In dunning, the flow waits for `payment-updated`
and `cancel-requested` at the same time. Each branch has its own sealed continuation, and both branches
race the backoff timer:

- `PaymentUpdated` tries the charge again.
- `CancellationRequested` ends the run.

The two events have opposite meanings, so they have separate event names. Thus a cancellation that arrives
together with a payment update is not lost. Both buttons are on the subscription card.

The HTTP API of the panel exposes `IMembershipManager` through the trigger controller above.
`IMembershipManager` is in its own assembly, `PiiMaker.Manager.Membership.Interface`, together with the
governed-step contracts. Each UI button sends one trigger case. The only code that is specific to a runtime
is the seam wiring of the host, below the trigger.

The seam wiring is one `IWorkflowGateway` (+`WorkflowSealer`) for each flow:

- `InProcWorkflowGateway`
- `TemporalWorkflowGateway`
- `DurableTaskWorkflowGateway`: a portable flow, or a native orchestration through its input factory.
- `ElsaWorkflowGateway`: start and resume by correlation id. It is durable across the **Restart host**
  action of the Elsa panel.
- `RestateWorkflowGateway`: HTTP ingress. The event with no payload crosses the language boundary into
  the Restate sidecar.
- `ZeebeWorkflowGateway`: creates the BPMN process instance and publishes the correlated message.

Some native flows use a small gateway that the consumer wrote: `NativeOffboardGateway` (Temporal) and
`RestateOffboardGateway` (Restate) start native offboarding.

The demo code above the seam starts at the entrypoint. It is identical on each host.

## Hosts

Each host is the consumer composition root for one cell. It wires `MembershipManager` (operation by name)
onto one runtime and one consumption model, and serves the control panel. To start a host, run
`dotnet run --project examples/PiiMaker/Hosts/<host> -- <port>`. All six hosts ship, and you can run each
of them.

- [`PiiMaker/Hosts/InProc`](PiiMaker/Hosts/InProc) (`:5001`): InProc, portable flow. It runs onboarding (A),
  subscription renewal (B), and the erasure sweep (D). It needs no backend and no Docker. Offboarding (C)
  is native-only, so this host cannot run it on the portable flow. Its card shows as disabled.
- [`PiiMaker/Hosts/Elsa`](PiiMaker/Hosts/Elsa) (`:5005`): onboarding (A) on Elsa, native flow. It is
  durable through EF Core SQLite. It needs no Docker, because SQLite is a file. The durability demo is
  interactive:
  1. Start onboarding. The flow parks on the invite-accepted bookmark, and Elsa persists the bookmark to SQLite.
  2. Push "Restart host". The host disposes the provider and builds a new one over the same database.
  3. Send invite-accepted. The saga resumes on the new provider and shreds. The host process stays the same.
- [`PiiMaker/Hosts/Temporal`](PiiMaker/Hosts/Temporal) (`:5002`): all flows on a Temporal server
  (`localhost:7233`, Docker required). It runs onboarding (A, portable), subscription renewal (B, portable
  continue-as-new), offboarding (C, native fan-out), and the erasure sweep (D). A small `IWorkflowGateway`
  over the consumer's `NativeOffboardWorkflow` starts native offboarding. The generic gateway starts the
  portable flow.
- [`PiiMaker/Hosts/DurableTask`](PiiMaker/Hosts/DurableTask) (`:5003`): all flows on the modern Durable
  Task SDK against a Durable Task Scheduler (`localhost:8080`, the DTS emulator in Docker). It runs
  onboarding (A, native), subscription renewal (B, portable continue-as-new on the scheduler), offboarding
  (C, native fan-out), and the erasure sweep (D). The host uses one task hub. The orchestration name keeps
  the two consumption models apart. The portable flow (B) and the native orchestrations that the consumer
  wrote (A, C) run together.
- [`PiiMaker/Hosts/Restate`](PiiMaker/Hosts/Restate) (`:5004`): the cross-language runtime. Restate has no
  .NET SDK. Thus the durable flow runs out-of-process in a compiled Restate sidecar that restate-server
  hosts. The sidecar calls back over HTTP to a small .NET host for the governed step. This example has its
  own sidecar ([`sidecar-rs/`](PiiMaker/Hosts/Restate/sidecar-rs)), separate from the library/test sidecar.
  If the sidecar binary is missing, the host builds it. The host then starts it. The panel operates all flows across the language boundary:
  - Onboarding (A) uses the `MembershipOnboard` flow that the consumer wrote.
  - Offboarding (C) uses the `MembershipOffboard` parallel fan-out.
  - A and C call one long-lived `/gov-step` host. The host routes by the prefix of the instance id.
  - Subscription renewal (B) uses the generic `MembershipPortable` flow with continue-as-new and dunning.
    It calls its own `/step`+`/terminate` host on a separate port. The `PORTABLE_STEP_URL` of the sidecar
    points to that port, so the two callback hosts run together.
  - The erasure sweep (D).

  This host needs restate-server (`:8088`/`:9070`, Docker), and cargo to build the sidecar.
- [`PiiMaker/Hosts/Zeebe`](PiiMaker/Hosts/Zeebe) (`:5006`): onboarding (A) on Camunda 8 / Zeebe as a native
  BPMN flow, and the erasure sweep (D). The flow is `bpmn/membership-onboard.bpmn`, and the host deploys it to the broker at startup.
  Zeebe is native-only and has no portable flow. A governed service-task job runs each governed `Onboard`
  step. A process end execution-listener runs the crypto-shred termination. This host needs Camunda 8 Run
  (gateway `:26500`, Operate `:8090`).

## Coverage matrix

Each example host wires `MembershipManager` (operation by name) onto one cell:

| Flow | Modes | Runtimes (on the control panels) |
|---|---|---|
| A Onboarding | native + portable | InProc (portable) · Temporal (portable) · DurableTask (native) · Elsa (native) · Restate (native) · Zeebe (native) |
| B Subscription | portable (the Manager also has a native `Renew`; no host runs it) | InProc · Temporal · DurableTask · Restate |
| C Offboarding | native only | Temporal · DurableTask · Restate |
| D Erasure | operation (mode-agnostic) | every host: runs over in-flight instances; the termination shred is each runtime's hook |

Each of the six runtimes has a web control panel that you can run. Zeebe is native-only and runs
onboarding and the erasure sweep only. The ports and the run instructions are above.
