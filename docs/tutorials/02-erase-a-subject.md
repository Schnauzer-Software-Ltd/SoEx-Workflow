> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Tutorial 2: Erase a subject

In [Tutorial 1](01-your-first-workflow.md), the workflow completed and crypto-shredded itself. This
tutorial shows the main use of SoEx.Workflow. A person uses their right to be forgotten while an
instance is in flight. You send a request to forget the person. Then SoEx does these operations:

1. It force-terminates the instance.
2. It keeps the data that the law requires you to retain.
3. It makes all other data unrecoverable.

Continue in the `FirstWorkflow` project from Tutorial 1. The tutorial takes approximately 15 minutes.

## Step 1: Retain what you must, before the shred

Erasure destroys the per-instance key. All data that is sealed with that key then becomes
unrecoverable. The law can require you to keep some data, for example a lawful-basis record or an audit
marker. The `OnRetaining` hook fires before the shred, while the data is readable. In that hook, write
the data that you must retain to your own store.

1. In `OnboardManager`, replace the empty `OnRetaining` with an operation that writes a record.

   In this tutorial, a small in-memory list is the store. The tutorial prints the list at the end.

```csharp
public sealed class OnboardManager : IOnboardManager, IErasureEvent
{
    public List<string> Retained { get; } = new();   // stands in for your own governed store

    public Task<WorkflowAction> Run(OnboardStep step) => /* …unchanged from Tutorial 1… */;

    // Pre-shred extract. Write must-retain data outward — never PII, and never into the result.
    // Must be idempotent on context.IdempotencyKey.
    public Task OnRetaining(RetainingContext context)
    {
        Retained.Add($"{context.IdempotencyKey}: onboarding record (lawful basis: contract)");
        return Task.CompletedTask;
    }

    public Task OnTerminated(TerminatedContext c) => Task.CompletedTask;
    public Task OnRetentionHeld(RetentionHeldContext c) => Task.CompletedTask;
}
```

## Step 2: Stand up an in-flight instance

An instance in flight has a per-instance key, and its subject is in the subject index. In this step, you
make that state directly, so the example needs no other parts. You also seal a payload. At the end, the
tutorial uses this payload to show that it is no longer readable.

This tutorial uses one subject: the person who starts the flow. An instance can hold more than one
subject. When a step learns about a person, the step declares that person. The erasure in this tutorial
then applies to that person also. This feature is
[`WorkflowAction.Subjects`](../reference/workflow-action.md#enrolling-a-subject-the-step-learned). This
tutorial does not use it.

1. Keep the governed-core wiring from Tutorial 1, Step 3.

   The wiring gives you `keys`, `index`, `step`, and `component`.
2. Remove the code from Tutorial 1, Step 4 and Step 5.

   That code declares `instanceId` and `ambient`. The code below declares these names again.
3. Add this code after the wiring.

```csharp
const string instanceId = "onboard-1";
const string subject    = "invitee@example.com";

byte[] ambient = WorkflowEnvelope.AmbientFor(step.Serializer, SubjectContext.Managed(subject))!;

// SealStep mints the per-instance key and encrypts the payload under it.
byte[] sealedPayload = step.SealStep(instanceId, new OnboardStep.Invite(subject, "res-1"), ambient);

// A governed step would also index the subject; we record that edge directly.
index.AddEdge(subject, instanceId);

Console.WriteLine($"Before erasure — key live: {keys.Has(instanceId)}");
Console.WriteLine($"Before erasure — payload readable: {CanDecrypt(step, instanceId, sealedPayload)}");
```

4. Add this helper as a top-level local function **above** the `OnboardStep` and `OnboardManager` type
   declarations.

   C# requires all top-level statements before the type declarations of the file. The type
   declarations are at the bottom of `Program.cs`.

```csharp
static bool CanDecrypt(GovernedStep<IOnboardManager> step, string id, byte[] sealedPayload)
{
    try { _ = step.AmbientOf(id, sealedPayload); return true; }
    catch (InvalidOperationException) { return false; }   // thrown once the key is gone
}
```

## Step 3: Issue the erasure request

`ErasureCoordinator` runs a request to forget a subject from start to end. It does these operations:

1. It uses the subject index to find each instance that holds the subject.
2. For each instance, it decides to let the instance finish or to force-terminate it.
3. It drives the terminations to crypto-shred.
4. It reports the result.

To send the request, do these steps:

1. Make an `ErasureCoordinator`.
2. Make an `ErasureRequest` for the subject.
3. Call `EraseAsync` and print each outcome.

```csharp
var coordinator = new ErasureCoordinator(
    index,
    new StatutoryDeadlineClock(),          // no policy → a conservative default window
    new ErasurePlanner(),
    new TerminationCoordinator(keys, index),
    new ErasureReporter());

var request = ErasureRequest.For("req-1", DateTimeOffset.UtcNow, subject);

// resolve maps each found instance id to what the coordinator needs to erase it.
// MaxRemainingDuration: null means "unbounded" → force-terminate now.
ErasureResult result = await coordinator.EraseAsync(request, id =>
    new ErasureTarget(id, component, new IdempotencyKey(id, "terminal", 0), MaxRemainingDuration: null));

foreach (var o in result.Outcomes)
    Console.WriteLine($"Erased {o.InstanceId}: {o.Action} → {o.State}");
```

## Step 4: See the data that stays and the data that is erased

1. Print the state of the key, the payload, and the retained store.

```csharp
Console.WriteLine($"After erasure — key live: {keys.Has(instanceId)}");
Console.WriteLine($"After erasure — payload readable: {CanDecrypt(step, instanceId, sealedPayload)}");
Console.WriteLine($"Retained outward: {string.Join("; ", component.Retained)}");
```

## Run it

1. Run the program.

```sh
dotnet run
```

After the host startup logs, you see this output:

```
Before erasure — key live: True
Before erasure — payload readable: True
Erased onboard-1: ForceTerminate → Complete
After erasure — key live: False
After erasure — payload readable: False
Retained outward: onboard-1/terminal/0: onboarding record (lawful basis: contract)
```

## What happened

The request named a subject. The coordinator found the subject in the index and found `onboard-1`. Then
it force-terminated the instance in this sequence:

1. `OnRetaining` fired. It wrote the lawful-basis record to your own store.
2. SoEx destroyed the per-instance key.
3. SoEx removed the subject from the index.

The key was the only way to read the sealed payload. Now nobody can decrypt the payload. This is
crypto-shred: you destroy the one key that makes the data readable. All sealed copies of the data then become
unrecoverable.

The work has two parts:

- You decide what data to retain. In `OnRetaining`, you write it to your own store, with no PII.
- SoEx destroys the key, removes the subject from the index, and reports. It does this the same way on
  each runtime.

## Next

- [How crypto-shred and erasure work](../explanation/crypto-shred-and-erasure.md). The model, the
  guarantees, and the threat model for this tutorial.
- [Run erasure maintenance](../how-to/run-erasure-maintenance.md). The maintenance tasks that close
  instances that have no erasure request.
- [Make crypto-shred durable](../how-to/make-crypto-shred-durable.md). Replace the in-memory key store
  with one that keeps its data after a restart. Then the shred is effective in production.
