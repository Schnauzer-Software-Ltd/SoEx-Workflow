> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Reference — erasure events

A step component hosted on a workflow binding must implement `IErasureEvents`. The wiring enforces
this via `WorkflowRegistration.RequireErasureEvents(...)`, an opt-in check that throws at wiring time
(composition-root runtime) if the component doesn't implement it, rather than silently running a no-op
termination. It is not a compile-time failure: `GovernedTermination` accepts null contracts, so the
guarantee comes from calling `RequireErasureEvents(...)` at composition. Namespace: `SoEx.Workflow`.

```csharp
public interface IErasureEvents
{
    Task OnRetaining(RetainingContext context);
    Task OnTerminated(TerminatedContext context);
    Task OnRetentionHeld(RetentionHeldContext context);
}
```

## Hooks

| Hook | When it fires | What you do |
|---|---|---|
| `OnRetaining(RetainingContext)` | Pre-shred, while the payload is still readable, on every termination path (natural completion and erasure). | Extract must-retain data and write it outward to your own store. Must be idempotent on `context.IdempotencyKey`. Never write PII into the result. |
| `OnTerminated(TerminatedContext)` | Post-termination, post-shred. | PII-free bookkeeping — audit, release locks. |
| `OnRetentionHeld(RetentionHeldContext)` | Extraction failed past the retry boundary (non-final). | The key is kept, auto-retry stopped, the instance flagged for an audited re-drive. Record/alert as you need. |

## Lifecycle order

```
OnRetaining (succeeds) ──▶ destroy key (crypto-shred) ──▶ prune subject index ──▶ OnTerminated
OnRetaining (fails)    ──▶ key retained ──▶ OnRetentionHeld   (quarantine; re-drive later)
```

The per-instance key is minted on first use and hard-deleted at termination. Once destroyed, anything sealed
under it is unrecoverable.

## Context types

- `RetainingContext` — carries `IdempotencyKey` (the `(InstanceId, name, sequence)` triple), so your
  outward write can be made idempotent.
- `TerminatedContext` — post-shred context for bookkeeping.
- `RetentionHeldContext` — held-instance context for quarantine handling; its `LastError` is the scrubbed,
  subject-free failure message (the same string the held log records), safe to log or alert on.

## See also

- [How to write a step component](../how-to/write-a-step-component.md) — implementing these in context.
- [Crypto-shred and erasure](../explanation/crypto-shred-and-erasure.md) — why retained data goes outward.
- [Erasure API](erasure-api.md) — driving erasure requests and the maintenance passes.
