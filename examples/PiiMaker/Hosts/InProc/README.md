> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Host: InProc · portable flow

InProc is the runtime that SoEx supplies. It always uses the portable flow. It needs no external backend
and no Docker. The host is a web control panel on port 5001. It runs until you stop it. You operate each
flow from the browser, one event at a time. The panel has cards for these flows:

- **A Onboarding**: `LookupUser → CreateAccount → wait("account-verified") → ReserveSubscription →
  SendInvite → wait("invite-accepted") → AssignSubscription → Complete`. If the user exists already,
  `LookupUser` goes directly to `ReserveSubscription`. The continuation of each wait is a sealed step that
  the signal contains. The flow arms it before the wait. The first reservation in a host process is
  `res-1`. Use the **Account verified** and **Invite accepted** buttons to send the events.
- **B Subscription**: `Charge` succeeds → continue-as-new into the next period (`Loop`) → `Complete`
  after the configured renewal periods. A declined charge starts dunning: a backoff, then a wait for
  `payment-updated` or `cancel-requested`. When the dunning attempts are exhausted, the flow cancels. Use
  **Force decline (period 1)** before **Start renewal** to see dunning.
- **D Erasure**: the **Forget subject** button sends an erasure request for the subject. The host admits
  the request, then drains it at once. The drain uses `ErasureCoordinator`. Each in-flight instance of the
  subject goes through a forced termination: the key is shredded and the index is pruned.

C Offboarding is a parallel revocation fan-out. The portable flow is sequential and cannot express a
fan-out. Thus offboarding is native-only, and it is on the hosts for the native flow. On this host, the
offboarding card shows as disabled, with a note.

## Run

```bash
dotnet run --project examples/PiiMaker/Hosts/InProc/PiiMaker.Host.InProc.csproj
```

To use a different port, add it after `--`, for example `-- 5011`. The host prints the address of the
panel:

```
PiiMaker InProc control panel → http://localhost:5001
```

Open that address in a browser. When a flow completes or is erased, the panel shows its instance as
"shredded".

## What it shows

- **Wiring** (`Program.cs` and `PiiMaker.Hosting.MembershipSystem.Compose`): the shared composition hosts
  a `WorkflowBinding<I>` and resolves the endpoint and the serializer one time. Each flow then builds a
  `GovernedStep` bound to its operation by name. One Membership entrypoint with many operations makes this
  possible. The wiring follows the
  [governed-core wiring](../../../../docs/reference/governed-core.md#the-wiring-sequence).
- **Governance in each flow**: the subject is in the sealed seed. The results and the event names contain
  no PII. At termination, the key is crypto-shredded and the subject index is pruned. PII that the system
  must retain goes out in `OnRetaining`. The host gets this PII for each instance from the subject index.
  The flow never returns it.
