> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Reference — erasure events

`IErasureEvent` is the interface for the termination hooks of a step component. A step component hosted on
a workflow binding must implement it. The namespace is `SoEx.Workflow`.

```csharp
public interface IErasureEvent
{
    Task OnRetaining(RetainingContext context);
    Task OnTerminated(TerminatedContext context);
    Task OnRetentionHeld(RetentionHeldContext context);
}
```

`WorkflowRegistration.RequireErasureEvent(...)` enforces this rule at wiring time, when the composition
root runs. It is an opt-in check. If the component does not implement `IErasureEvent`, the check throws. A
missing implementation is not a compile-time failure, because `GovernedTermination` accepts null
contracts. Call `RequireErasureEvent(...)` at composition to get the guarantee. Without the check, a
component that does not implement `IErasureEvent` gets a no-op termination with no error.

## Hooks

| Hook | When it fires | What you do |
|---|---|---|
| `OnRetaining(RetainingContext)` | Before the shred, while the payload is readable, on each termination path (natural completion and erasure). | Extract the data that you must keep, and write it to your own store. Make the hook idempotent on `context.IdempotencyKey`. Never write PII into the result. |
| `OnTerminated(TerminatedContext)` | After the termination and after the shred. | Do bookkeeping that contains no PII, for example audit or lock release. |
| `OnRetentionHeld(RetentionHeldContext)` | The extraction failed after the retry boundary. This state is not final. | The framework keeps the key, stops the automatic retry, and flags the instance for an audited re-drive. Record or alert as necessary. |

## Lifecycle order

```
OnRetaining (succeeds) ──▶ destroy key (crypto-shred) ──▶ prune subject index ──▶ OnTerminated
OnRetaining (fails)    ──▶ key retained ──▶ OnRetentionHeld   (quarantine; re-drive later)
```

The framework mints the per-instance key on first use. It hard-deletes the key at termination. After the
framework destroys the key, all data sealed with that key is unrecoverable.

## Context types

- `RetainingContext` carries `IdempotencyKey`, the `(InstanceId, name, sequence)` triple. Use it to make
  your write to your own store idempotent.
- `TerminatedContext` is the context for bookkeeping after the shred.
- `RetentionHeldContext` is the context for quarantine of a held instance. Its `LastError` is the
  scrubbed failure message with no subject in it. The held log records the same string. You can log it or
  alert on it safely.

## See also

- [How to write a step component](../how-to/write-a-step-component.md) shows how to implement these hooks.
- [Crypto-shred and erasure](../explanation/crypto-shred-and-erasure.md) gives the reason that you write
  retained data to your own store.
- [Erasure API](erasure-api.md) shows how to send erasure requests and run the maintenance passes.
