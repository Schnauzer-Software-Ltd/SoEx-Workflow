> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# How to write a step component

A step component is a SoEx component that does the work of one step at a time. You write it for both
consumption models. This guide shows the parts that are the same in the two models:

- make the step DTOs
- write the component
- attach the subjects
- implement the erasure events

The models differ only in the return type of the step operation. In the portable flow, the step
operation returns a [`WorkflowAction`](../reference/workflow-action.md). In a native flow, it returns a
business result.

## 1. Model the steps as DTOs

1. Make one DTO for each step.

   A step DTO holds the data for one step. The driver or the runtime sets the order of the steps.
2. Put the DTOs in a sealed hierarchy.

   With a sealed hierarchy, the dispatch covers all the step types.

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

1. Define a contract with one step operation. The operation takes your step DTO.

   You choose the name of the operation. The framework finds the operation in the contract.
2. Implement the contract as a usual component.

   SoEx calls your operation by name with the typed step. Your code does not read or make an envelope.

```csharp
using SoEx.Workflow;

public sealed record StepOutcome(string Step, int Effect);

public interface IOnboardManager
{
    Task<StepOutcome> Run(OnboardStep step);   // native: a business result
    // (portable model: Task<WorkflowAction> Run(OnboardStep step); — everything else is identical)
}
```

3. If an event can resume a step, and the raiser has data for that step, add a second parameter.

   Examples of such data: the person who accepted an invite, or the amount that cleared.

```csharp
Task<StepOutcome> Run(OnboardStep step, InviteAccepted? accepted = null);
```

The second parameter has a value only when a raise with data resumed the step. It has the value only
for that one dispatch. In all other calls, the value is null.

A step operation has one of two shapes:

- the step DTO
- the step DTO, then its event data

The host refuses all other shapes when you build it. If a raise carries data and your operation has no
parameter for it, the instance stops and holds. The instance keeps its key. To continue, extend the
component and re-drive the instance. See
[Receiving data with an event](../reference/workflow-action.md#receiving-data-with-an-event).

```csharp

public sealed class OnboardManager : IOnboardManager, IErasureEvent
{
    public Task<StepOutcome> Run(OnboardStep step)
    {
        // Each arm does this step's work in-process (calling collaborators if it has any)
        // and returns a business result. No flow, no envelope.
        return Task.FromResult(new StepOutcome(step.GetType().Name, Effect: 1));
    }

    // … IErasureEvent (step 4) …
}
```

A step component is a usual SoEx component. It can have zero or more dependencies. Inject
collaborators (accessors, engines) through the constructor, as for all SoEx components. Call them
in-process inside a step.

## 3. Attach the subject

The subject is the person whose PII (personally identifiable information) a step uses. SoEx indexes
each subject and sends erasure requests for it to the correct instances.

1. Make a `SubjectContext` for the subject.

   ```csharp
   SubjectContext.Managed("invitee@example.com");    // SoEx indexes + erases this subject
   SubjectContext.External("invitee@example.com");   // subject handling stays with your own system
   ```

2. Make the ambient bytes one time from the `SubjectContext` with `WorkflowEnvelope.AmbientFor`.
3. Give the ambient bytes to `SealStep` when you seal the first step.

   The sealed step carries the ambient bytes. Each step receives them on its `StepContext`.

```csharp
byte[] ambient = WorkflowEnvelope.AmbientFor(step.Serializer,
    SubjectContext.Managed("invitee@example.com"))!;

byte[] seed = step.SealStep(instanceId, new OnboardStep.Lookup("invitee@example.com"), ambient);
```

The subjects of an instance accumulate. The ambient bytes name the subject at the start of the flow.

4. If a step learns about a new person, declare that person on the action that the step returns.

   SoEx indexes the new subject and carries it to the subsequent steps.

```csharp
return new WorkflowAction.RaiseIntoNext(nextStep).Enrolling(billingContact);
```

See [`WorkflowAction`](../reference/workflow-action.md#enrolling-a-subject-the-step-learned).

## 4. Implement the erasure events

1. Implement `IErasureEvent` on each component that a workflow binding hosts.

   This declaration is mandatory. The wiring calls `WorkflowRegistration.RequireErasureEvent(...)`. If
   the declaration is missing, this call throws an exception at wiring time. Wiring time is the run
   time of the composition root. The compiler does not find the missing declaration. The check runs
   when you compose the host.

```csharp
public sealed class OnboardManager : IOnboardManager, IErasureEvent
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

The [erasure events reference](../reference/erasure-events.md) gives the exact context types.
[Crypto-shred and erasure](../explanation/crypto-shred-and-erasure.md) tells why retained data must go
to your own store.

## Keep PII out of clear-text values

SoEx writes two types of values to the journal in clear text. Keep PII out of both:

- **Names.** These are the instance id and the event and timer names. Make instance ids with
  [`DeterministicInstanceId`](trigger-flows-from-outside.md). Name events and timers by a kind that
  contains no PII.
- **The workflow result.** SoEx returns the result and writes it to the journal in clear text. Return
  a handle or a kind as the result, with no subject in it. Write the data that you must keep to your
  own store in `OnRetaining`.

SoEx scans both values for known subject ids. This scan is a safety net. You can
[make the scan stricter](customize-pii-detection.md). Design your names and results to contain no PII.

## Next

- Portable flow: [Run the portable flow](run-the-portable-flow.md).
- Native flow: [Author a native flow](author-a-native-flow.md).
- Wiring details: [The governed core reference](../reference/governed-core.md).
