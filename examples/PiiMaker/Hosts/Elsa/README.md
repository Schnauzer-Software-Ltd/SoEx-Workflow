# Host: Onboarding · Elsa · native flow · durable (SQLite)

The native consumption model on a durable Elsa host. The consumer authors the flow in Elsa's own model
(a registered workflow of activities + a bookmark wait), with each step a governed call to the
Membership entrypoint's native operation (`OnboardStep`).

Elsa is an in-process engine, not a server, so its "permanent" form is a persistence provider. This
host persists the workflow position to a SQLite store (EF Core) and proves durability the way Elsa
does: it runs on one host until it parks on the `invite-accepted` bookmark, disposes that host, then a
fresh host resumes the flow from the persisted store to completion. (The Temporal host is the server
analog; here the durable store is a SQLite file.)

## Run

No Docker needed (SQLite is a file under `/tmp`).

```bash
dotnet run --project examples/PiiMaker/Hosts/Elsa/PiiMaker.Host.Elsa.csproj
```

```
▶ onboarding invitee@example.com  (saga elsa-onboard-…)  —  Elsa · native flow · durable SQLite
  host 1 disposed — parked at invite-accepted: key live = True, subject indexed = 1

✔ resumed on a fresh host : status = Finished
✔ crypto-shred at termination: key present = False  (false ⇒ journal unrecoverable)
✔ subject index pruned    : instances for subject = 0
✔ must-retain written OUT : membership-record:invitee@example.com
```

## What it shows

- **Durable native flow** — `MembershipOnboardWorkflow` is a registered Elsa workflow; a fresh host
  rebuilds the identical definition and resumes the persisted bookmark. Activities carry no live object
  references — they resolve `GovernedStep`/`GovernedTermination` from DI and read the sealed seed from the
  workflow input — so a rehydrated instance works.
- **Sealed-seed threading** — the subject is sealed once into the seed; the flow threads only that
  ciphertext (in the workflow input) and recovers the subject through the framework inside each activity.
  Governance is anchored on the correlation id the seed was sealed under.
- **Durability across a restart** — the Elsa workflow position lives in SQLite; the per-instance key vault
  (in-memory here; a DB/HSM store in production) is the shared durable governance that survives the host
  restart. Crypto-shred + index prune happen at the termination on the post-restart host.
