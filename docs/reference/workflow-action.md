> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Reference: `WorkflowAction`

`WorkflowAction` is the value that a step operation of the portable flow returns. The driver maps the
action to the durable primitives of the runtime. The framework puts the typed step and result payloads in
an envelope. Thus, you give DTOs to the action, and the framework makes the bytes. Namespace:
`SoEx.Workflow`.

## Actions

| Action | Meaning |
|---|---|
| `Complete(object? Result)` | The instance is complete. `Result` is your typed result. The journal keeps it in clear text, so keep PII out of it. |
| `RaiseIntoNext(object NextStep)` | Sends the typed `NextStep` DTO to the next step. Use it to move saga state forward. |
| `WaitForEvent(IReadOnlyList<EventBranch> Branches, TimeSpan? Timeout = null, object? OnTimeout = null)` | Parks the instance until a raise of the event of one branch. If you give a `Timeout`, a durable timer races the branches. If the timer wins, the instance resumes into the `OnTimeout` step. If a branch wins, the branch sets the next step, as the table in [Resume after a wait](#resume-after-a-wait) shows. The framework seals each continuation at wait time and the journal keeps it. |
| `EventBranch(string EventName, object? OnEvent = null)` | One event that can resume a wait. It has the event name and the step that a raise of that name resumes into. |
| `Delay(TimeSpan Duration)` | Parks the instance on a durable timer. |
| `Loop(object CarryState)` | Does a continue-as-new and carries the typed `CarryState` to the new run. |

Each action also has `Subjects`. `Subjects` contains the persons that this step found during its run.
See [Enrolling a subject the step learned](#enrolling-a-subject-the-step-learned).

## Resume after a wait

When a `WaitForEvent` resumes, the branch of the raised event sets the next step. If that branch declares
an `OnEvent` step, that step runs. This rule applies to a bare raise and to a raise with data. Your step
operation receives the data as its second argument. If the branch declares no `OnEvent` step, the raiser
gives the next step.

| Branch declared `OnEvent` | Raise carried | What runs |
|---|---|---|
| yes | data | the branch's `OnEvent` step, receiving the data |
| yes | nothing | the branch's `OnEvent` step |
| no | data | the raised payload, as the next step |
| no | nothing | nothing (the framework rejects the raise) |

See [Receiving data with an event](#receiving-data-with-an-event).

## Wait for more than one event

A wait can have many branches, one for each event. Each branch states the next step for a raise of its
event name. Examples:

- An onboarding flow waits for an email verification. It also has a resend button.
- An approval waits for a decision. A user can also cancel or escalate it.

```csharp
return new WorkflowAction.WaitForEvent(
    [
        new EventBranch("verified", new OnboardStep.Provision(orgId, userId)),
        new EventBranch("resend",   new OnboardStep.SendCode(orgId, userId, attempt + 1)),
    ],
    TimeSpan.FromHours(72),
    OnTimeout: new OnboardStep.Abandon("code expired"));
```

All branches race each other and the timer. Give each meaning its own event name. If one event name has
two meanings, the flow must use the presence of a payload to identify the meaning. That method fails when
both senders can raise the event bare.

The order of the branches sets the winner when two or more events are already deliverable at the time the
wait arms. The first declared branch wins on each runtime. Thus, your flow sets the winner. The delivery
order of the runtime has no effect.

The branches of one wait must have different event names. On each runtime, the event name is the delivery
key. At resume, the runtime can identify a branch only by its name. The constructor rejects duplicate
names.

## Receiving data with an event

A raiser can have data that the flow did not have when it parked. Examples:

- A different person accepts an invite.
- A payment clears for an amount that is different from the quoted amount.

The flow sets the next step. It sealed that step at wait time. The raiser gives only its data.

To receive the data, declare a second parameter on your step operation:

```csharp
public interface IOnboardManager
{
    Task<WorkflowAction> Step(OnboardStep step, InviteAccepted? accepted = null);
}
```

The parameter has a value only on a step that a raise with data resumed into. It has the value for that
one dispatch only. Later steps do not receive the data. On the raise side, seal the data with
`SealEventData`. `SealEventData` is a different seal from the `Seal` that gives a step:

```csharp
await gateway.RaiseEventAsync(instanceId, "invite-accepted",
    sealer.SealEventData(instanceId, new InviteAccepted(whoAccepted)));
```

These rules apply:

- **Use the correct seal.** If you seal a step where the flow expects data, or data where it expects a
  step, the framework refuses the payload. The error message names both types.
- **Declare the parameter before you raise data at an operation.** If the operation has no second
  parameter, the raise fails. The instance parks and the framework keeps its key. The continuation does not
  run. After the component declares the parameter, re-drive the instance. In this one case, data on an
  existing raise changes the behavior of a flow that is in flight.

Event data has no ambient context. The subject context of the flow travels on the continuation from the
seed. To enroll a subject that the raise tells you about, use `Subjects`. The event data does not enroll a
subject.

## Notes

- `OnEvent` is the branch-level equivalent of `OnTimeout`. With `OnEvent`, a bare event resumes a wait into
  a step that the flow selected before. A bare event has no payload and no key material. See
  [Trigger flows from outside](../how-to/trigger-flows-from-outside.md#raise-an-event-with-no-payload).
- `Loop` carries the logical instance id and the per-instance key to the new run. The framework seals the
  carried state as it seals all other journaled payloads.
- A branch with no `OnEvent` rejects a bare raise at that name, because the flow declared no meaning for
  it. The branch accepts a payload, and the payload becomes the next step.
- The `OnTimeout` path has no event data, because there was no raise.
- `WaitForEvent` has one constructor only. The action goes through the message serializer of your host as
  a polymorphic response. The serializer must have one unambiguous constructor to rebuild the value. Thus,
  a convenience overload with one name is not possible.

## Enrolling a subject the step learned

A flow starts with the subject that its caller knew. A step can find a different subject. Examples:

- A lookup returns the billing contact of the account.
- A claim names a dependant.

The step has this knowledge. Thus, the step declares the subject on the action that it returns:

```csharp
return new WorkflowAction.RaiseIntoNext(new PolicyStep.Notify(policyId))
    .Enrolling(billingContact);
```

Before the framework flattens the action for the journal, it adds those subjects to the subject context of
the step. This has two results:

- An erasure request for that person now finds this instance. This is also true while the instance is
  parked on a wait for many days.
- From this step on, the guard removes the subject from each name that the runtime journals in clear text.
  The guard does the same for the start subject of the flow.

The subject does not go into the journal. It travels on the sealed continuation, which crypto-shred
destroys. The flattened action that a runtime records contains only the kind, the event names, and sealed
bytes.

Declare the subject on an action that continues the flow: `RaiseIntoNext`, `WaitForEvent`, `Loop`, or
`Complete`. A `Delay` seals no next step, so the subject has nothing to travel on. Thus, the framework
rejects an enrollment on a `Delay`. It does not apply part of the enrollment.

Two limits apply:

- The guard applies to names from this step forward. The framework checked earlier names of this instance
  against the subjects that it knew at that time. A new enrollment does not examine these names again.
- An externally-managed flow (`SubjectContext.External`) leaves the indexing to your own system. This also
  applies to its start subject. The framework still carries the subject, so the name guards include it.

To read the enrollment back, use `InstancesForAsync` on the subsystem face of the utility. Its purpose is a
subject that a flow found during its run. The instance id comes from the start subject of the flow. Thus,
a derivation from the later subject cannot find the instance.
