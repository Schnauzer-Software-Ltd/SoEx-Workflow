# Host: InProc · portable flow

The InProc runtime is the SoEx-provided runtime: always portable, no external backend, no Docker. This
host runs every flow that has a portable operation, plus the mode-agnostic erasure sweep:

- **A Onboarding** — `LookupUser → CreateAccount → wait("account-verified") → ReserveSubscription →
  SendInvite → wait("invite-accepted") → AssignSubscription → Complete`. Each wait's continuation is a
  sealed step the signal carries (pre-armed; `res-1` is the deterministic first reservation).
- **B Subscription** — `Charge` succeeds → continue-as-new into the next period (`Loop`) → `Complete`
  after the configured renewal periods. (A declined charge would enter dunning: backoff + wait for
  `payment-updated`, escalating to cancel.)
- **D Erasure** — brings two instances in-flight for one subject, then runs `ErasureCoordinator`
  "forget subject S": each is force-terminated to its clean termination (key shredded, index pruned), and a
  subject-level report is returned.

C Offboarding is not here: it's a parallel revocation fan-out that the sequential portable flow can't
express, so it's native-only and appears on the native-flow hosts.

## Run

```bash
dotnet run --project examples/PiiMaker/Hosts/InProc/PiiMaker.Host.InProc.csproj
```

Expected output:

```
A onboarding  : "assigned:res-1"  |  key live after = False  |  subject indexed = 0  |  retained outward = 1
B subscription: "renewed:3"  |  key live after = False  |  subject indexed = 0  |  retained outward = 1

D erasure     : 'forget leaver@example.com' — 2 instances in-flight (keys live = 2, indexed = 2)
                  erase-a: ForceTerminate → Complete
                  erase-b: ForceTerminate → Complete
                  after sweep: keys live = 0, indexed = 0, report = SubjectLevel (default-policy window)
                  retained outward: membership-record:leaver@example.com; membership-record:leaver@example.com
```

## What it shows

- **Wiring** (`ComposeHost` in `Program.cs`) — host a `WorkflowBinding<I>`, start the host, resolve the
  endpoint + serializer once; each flow then builds a `GovernedStep` bound to its operation by name
  (the payoff of one multi-operation Membership entrypoint). Mirrors the
  [governed-core wiring](../../../../docs/reference/governed-core.md#the-wiring-sequence).
- **Governance, every flow** — the subject rides the sealed seed; results and event names are PII-free;
  at termination the key is crypto-shredded and the subject index pruned; must-retain PII is written
  outward in `OnRetaining` (recovered per-instance from the subject index), never returned.
