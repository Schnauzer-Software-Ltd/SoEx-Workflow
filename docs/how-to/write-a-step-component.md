> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# How to write a step component

A step component is the heart of what you write: a plain SoEx component that does one step's work at a
time. This guide covers the parts that are the same for both consumption models: modeling steps,
writing the component, attaching subjects, and implementing the erasure events. The only difference
between the models is the return type. In the portable model your step operation returns a
[`WorkflowAction`](../reference/workflow-action.md), while in a native flow it returns a business
result.

## 1. Model the steps as DTOs

Each step is a DTO carrying just what that step needs. Flow does not live in the DTO: there's no "next
step" field, because sequencing is the driver's or the backend's job. A sealed hierarchy keeps dispatch
exhaustive:

```csharp
public abstract record OnboardStep
{
    public sealed record Lookup(string Email) : OnboardStep;
    public sealed record Invite(string Email, string ReservationId) : OnboardStep;
    public sealed record Assign(string ReservationId, string User) : OnboardStep;
    public sealed record Release(string ReservationId) : OnboardStep;   // compensation
}
```

## 2. Write the component

Define a contract with one step operation taking your step DTO, and implement it as an ordinary
component. There's no envelope to crack or build; SoEx dispatches your operation by name with the typed
step. The operation name is yours, and the framework discovers it from the contract.

```csharp
using SoEx.Workflow;

public sealed record StepOutcome(string Step, int Effect);

public interface IOnboardSteps
{
    Task<StepOutcome> Run(OnboardStep step);   // native: a business result
    // (portable model: Task<WorkflowAction> Run(OnboardStep step); — everything else is identical)
}

public sealed class OnboardSteps : IOnboardSteps, IErasureEvents
{
    public Task<StepOutcome> Run(OnboardStep step)
    {
        // Each arm does this step's work in-process (calling collaborators if it has any)
        // and returns a business result. No flow, no envelope.
        return Task.FromResult(new StepOutcome(step.GetType().Name, Effect: 1));
    }

    // … IErasureEvents (step 4) …
}
```

A step component is a normal SoEx component, so it may have no dependencies or many.
Constructor-inject collaborators (accessors, engines) exactly as for any SoEx component, and call them
in-process inside a step.

## 3. Attach the subject

Tell SoEx which PII subject a step touches, so it can index that subject and route erasure for it.
Build the ambient bytes once and pass them on the step context:

```csharp
SubjectContext.Managed("invitee@example.com");    // SoEx indexes + erases this subject
SubjectContext.External("invitee@example.com");   // subject handling stays with your own system
```

Subjects are additive; a later step may name more.

## 4. Implement the erasure events

A component hosted on a workflow binding must implement `IErasureEvents`. This is a deliberate opt-in:
the wiring calls `WorkflowRegistration.RequireErasureEvents(...)`, so forgetting the declaration throws
at wiring time (composition-root runtime) rather than silently running a no-op termination. Note that
it is not a compile-time failure; the check runs when you compose the host.

```csharp
public sealed class OnboardSteps : IOnboardSteps, IErasureEvents
{
    // … Run(OnboardStep) …

    // Pre-shred extract: fires while the payload is still readable, on every termination path.
    // Write must-retain data outward to your own store, never into the result (it's journaled in
    // clear) and never PII. Must be idempotent on context.IdempotencyKey.
    public Task OnRetaining(RetainingContext context)
        => _retained.WriteAsync(context.IdempotencyKey, mustRetainRecord);

    // Post-termination, post-shred, PII-free bookkeeping (audit, release locks).
    public Task OnTerminated(TerminatedContext context) => Task.CompletedTask;

    // Extraction-failure quarantine (non-final): the key is kept, retry stopped, the instance
    // flagged for an audited re-drive.
    public Task OnRetentionHeld(RetentionHeldContext context) => Task.CompletedTask;
}
```

See the [erasure events reference](../reference/erasure-events.md) for the exact context types, and
[crypto-shred and erasure](../explanation/crypto-shred-and-erasure.md) for why retained data must go
outward.

## Two rules to keep PII out of the clear

SoEx journals two things in clear, so keep both PII-free:

- Names, meaning the instance id and event/timer names. Derive instance ids with
  [`DeterministicInstanceId`](trigger-flows-from-outside.md), and name events and timers by PII-free
  kind.
- The workflow result. It's returned and journaled in clear, so return a handle or a kind, not a
  subject, and write anything you must keep outward in `OnRetaining`.

SoEx guards both with a subject-id check as a safety net, and you can
[make that check stricter](customize-pii-detection.md). But keep them PII-free by construction.

## Next

- Portable model: [Run the portable flow](run-the-portable-flow.md).
- Native model: [Author a native flow](author-a-native-flow.md).
- Wiring details: [The governed core reference](../reference/governed-core.md).
