> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Reference — `WorkflowAction`

The value a portable-model step operation returns; the driver routes it onto the runtime's durable
primitives. The framework envelopes the typed step/result payloads, so you pass DTOs rather than raw
bytes. Namespace: `SoEx.Workflow`.

When a `WaitForEvent` resumes, the event payload becomes the next step; an event with no payload
resumes into the wait's `OnEvent` step.

| Action | Meaning |
|---|---|
| `Complete(object? Result)` | The instance is finished; `Result` is your typed result. Journaled in clear, so keep it PII-free. |
| `RaiseIntoNext(object NextStep)` | Route the typed `NextStep` DTO into the next step (thread saga state forward). |
| `WaitForEvent(string EventName, TimeSpan? Timeout = null, object? OnTimeout = null, object? OnEvent = null)` | Park until the named event. With `Timeout`, the event races a durable timer; if the timer wins, resume into the `OnTimeout` step. A payload-carrying event becomes the next step; an empty event resumes into the `OnEvent` step. Both continuations are sealed at wait time and journaled. |
| `Delay(TimeSpan Duration)` | Park on a durable timer. |
| `Loop(object CarryState)` | Continue-as-new, carrying the typed `CarryState` across the boundary. |

## Notes

- `OnEvent` is the symmetric twin of `OnTimeout`: it lets a bare event (no payload, no key material)
  resume a wait into a pre-decided step. See
  [Trigger flows from outside](../how-to/trigger-flows-from-outside.md#raise-an-event-with-no-payload).
- `Loop` carries the logical instance id and per-instance key across the continue-as-new boundary, and
  the carried state is sealed like any other journaled payload.
- A `WaitForEvent` with no `OnEvent` rejects a bare raise, because the flow declared no meaning for it.
