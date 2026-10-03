> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Host: Onboarding · Elsa · native flow · durable (SQLite)

This host uses the native consumption model on a durable Elsa host. The consumer writes the flow in the
model of Elsa: a registered workflow of activities and a bookmark wait. Each step is a governed call to the
native operation (`Onboard`) of the Membership entrypoint.

Elsa is an in-process engine. It has no server. A persistence provider makes it permanent. This host
persists the position of the workflow to a SQLite store (EF Core). The host is a web control panel on port
5005. It runs onboarding (A) and the erasure sweep (D). You show durability by hand, from the panel:

1. Push **Start onboarding**. The flow runs `lookup → reserve → invite` and parks on the
   `invite-accepted` bookmark. Elsa persists the bookmark to SQLite. This flow has no
   `account-verified` wait.
2. Push **Restart host** on the Durability card. The host disposes the Elsa provider and builds a new
   one over the same SQLite database.
3. Push **Invite accepted**. The flow resumes on the new provider and runs to completion.

The Temporal host is the server equivalent. Here, the durable store is a SQLite file.

## Run

This host needs no Docker. SQLite is a file under `/tmp`.

```bash
dotnet run --project examples/PiiMaker/Hosts/Elsa/PiiMaker.Host.Elsa.csproj
```

The host prints the address of the panel and the path of the SQLite file. Open the address in a browser:

```
PiiMaker Elsa control panel → http://localhost:5005  (durable SQLite: /tmp/piimaker-elsa-<guid>.db)
```

Each start of the host process makes a new SQLite file with a new name. When the process stops, it
deletes that file. Thus a flow continues across **Restart host**, but it does not continue across a stop
and a new start of the process.

## What it shows

- **Durable native flow**: `MembershipOnboardWorkflow` is a registered Elsa workflow. A new provider builds the
  identical definition again and resumes the persisted bookmark. The activities hold no references to live
  objects. They resolve `GovernedStep`/`GovernedTermination` from DI and read the sealed seed from the
  workflow input. Thus a rehydrated instance works.
- **The sealed seed through the flow**: the framework seals the subject into the seed one time. The flow
  passes only that ciphertext, in the workflow input. Each activity gets the subject back through the
  framework. Governance uses the correlation id under which the framework sealed the seed.
- **Durability across a restart**: SQLite holds the position of the Elsa workflow. The store of
  per-instance keys is the shared durable governance. **Restart host** replaces only the Elsa provider, so
  the key store and the subject index stay in the same process. By default, the key store is in memory.
  In production, it is a DB or HSM store. Crypto-shred and the index prune occur at the termination on the
  new provider.
