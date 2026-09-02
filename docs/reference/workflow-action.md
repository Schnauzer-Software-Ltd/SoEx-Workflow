> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Reference — `WorkflowAction`

The value a portable-model step operation returns; the driver routes it onto the runtime's durable
primitives. The framework envelopes the typed step/result payloads, so you pass DTOs rather than raw
bytes. Namespace: `SoEx.Workflow`.

When a `WaitForEvent` resumes, the event payload becomes the next step. An event raised with no payload
resumes into the `OnEvent` step of the branch it was raised at.

| Action | Meaning |
|---|---|
| `Complete(object? Result)` | The instance is finished; `Result` is your typed result. Journaled in clear, so keep it PII-free. |
| `RaiseIntoNext(object NextStep)` | Route the typed `NextStep` DTO into the next step (thread saga state forward). |
| `WaitForEvent(IReadOnlyList<EventBranch> Branches, TimeSpan? Timeout = null, object? OnTimeout = null)` | Park until one of the branches' events is raised. With `Timeout`, they race a durable timer; if the timer wins, resume into the `OnTimeout` step. A payload-carrying event becomes the next step; an empty event resumes into that branch's `OnEvent` step. Every continuation is sealed at wait time and journaled. |
| `EventBranch(string EventName, object? OnEvent = null)` | One way a wait can be resumed: the event name, and the step a bare raise of that name means. |
| `Delay(TimeSpan Duration)` | Park on a durable timer. |
| `Loop(object CarryState)` | Continue-as-new, carrying the typed `CarryState` across the boundary. |

Every action also carries `Subjects`, the people this step learned about while it ran. See
[Enrolling a subject the step learned](#enrolling-a-subject-the-step-learned).

## Waiting on more than one event

A parked instance often has more than one thing that can happen to it. An onboarding flow waiting for
an email verification may also offer a resend button; an approval may also be cancelled or escalated.
Give the wait one branch per event, and each branch says what a bare raise of its own name means:

```csharp
return new WorkflowAction.WaitForEvent(
    [
        new EventBranch("verified", new OnboardStep.Provision(orgId, userId)),
        new EventBranch("resend",   new OnboardStep.SendCode(orgId, userId, attempt + 1)),
    ],
    TimeSpan.FromHours(72),
    OnTimeout: new OnboardStep.Abandon("code expired"));
```

All branches race each other and the timer. The alternative is to overload one event name for two
meanings and tell them apart by whether a payload came with it, which stops working as soon as both
senders can raise the event bare.

Branch order decides the winner when more than one of the events is already deliverable at the moment
the wait arms. The first branch declared wins, on every runtime, so the choice is a property of your
flow rather than of the engine's delivery order.

Two branches of one wait cannot share an event name. The name is the delivery key on every runtime, so
duplicates could not be told apart at resume; the constructor rejects them.

## Notes

- `OnEvent` is the branch-level twin of `OnTimeout`: it lets a bare event (no payload, no key material)
  resume a wait into a pre-decided step. See
  [Trigger flows from outside](../how-to/trigger-flows-from-outside.md#raise-an-event-with-no-payload).
- `Loop` carries the logical instance id and per-instance key across the continue-as-new boundary, and
  the carried state is sealed like any other journaled payload.
- A branch with no `OnEvent` rejects a bare raise at that name, because the flow declared no meaning
  for it. The branch still accepts a payload-carrying raise.
- `WaitForEvent` has a single constructor by design. The action travels through your host's message
  serializer as a polymorphic response, and a second public constructor leaves the serializer no
  unambiguous way to rebuild the value, so a one-name convenience overload cannot exist.

## Enrolling a subject the step learned

The subject a flow starts with is the one its caller knew. A step often discovers another: a lookup
returns the account's billing contact, a claim names a dependant. The step is what knows, so the step
says so, on the action it returns.

```csharp
return new WorkflowAction.RaiseIntoNext(new PolicyStep.Notify(policyId))
    .Enrolling(billingContact);
```

Before the action is flattened for the journal, the framework folds those subjects into the step's
subject context. Two things follow from that. An erasure request for that person now reaches this
instance, including while it sits parked on a wait for days. And from this step on the subject is
guarded out of every name the runtime journals in clear, the same as the subject the flow started with.

The subject itself never reaches the journal. It travels on the sealed continuation, which the
crypto-shred can reach. The flattened action a runtime records carries only the kind, the event names,
and sealed bytes.

Declare it on an action that continues the flow: `RaiseIntoNext`, `WaitForEvent`, `Loop` or `Complete`.
A `Delay` seals no next step for the subject to travel on, so an enrollment on one is rejected rather
than half applied.

Two limits worth knowing. The guard is prospective: names already journaled for this instance were
checked against the subjects known at the time, and enrolling someone now does not re-examine them. And
an externally-managed flow (`SubjectContext.External`) keeps deferring indexing to your own system, as
it does for the subject it started with; the subject is still carried, so the name guards cover it.

Reading the enrollment back is the job of `InstancesForAsync` on the utility's subsystem face. A
subject learned mid-run is the case it exists for: the instance id was derived from the subject the flow
started with, so no derivation from the person you learned about later reaches it.
