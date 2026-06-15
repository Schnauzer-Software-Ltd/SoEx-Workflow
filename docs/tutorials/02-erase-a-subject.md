> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Tutorial 2 — Erase a subject

In [Tutorial 1](01-your-first-workflow.md) your workflow completed naturally and crypto-shredded itself.
SoEx.Workflow exists for the harder case, though: someone exercises their right to be forgotten while
an instance is still in flight. In this tutorial you issue a "forget this person" request and watch
SoEx force-terminate the instance, keep the data you're legally required to retain, and render everything
else unrecoverable.

Continue in the same `FirstWorkflow` project from Tutorial 1. This takes about 15 minutes.

## Step 1 — Retain what you must, before the shred

Erasure destroys the per-instance key, so anything sealed under it is gone. Sometimes you're legally
required to keep something: a lawful-basis record, an audit marker. The `OnRetaining` hook fires before
the shred, while the data is still readable. That's where you write must-retain data outward to your own
store.

Replace the no-op `OnRetaining` in `OnboardManager` with one that records outward. We'll keep a tiny
in-memory store so we can print it later:

```csharp
public sealed class OnboardManager : IOnboardManager, IErasureEvents
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

## Step 2 — Stand up an in-flight instance

A running instance has already minted its per-instance key and registered its subject in the index.
We'll set that state up directly so the example is self-contained, and seal a payload so we can prove,
at the end, that it becomes unreadable.

Keep the governed-core wiring from Tutorial 1 (it gives you `keys`, `index`, `step`, and `component`).
Then add:

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

Add this helper as a top-level local function. It has to live with the other top-level statements —
**above** the `OnboardStep`/`OnboardManager` type declarations that sit at the bottom of `Program.cs` —
because C# requires every top-level statement to precede the file's type declarations:

```csharp
static bool CanDecrypt(GovernedStep<IOnboardManager> step, string id, byte[] sealedPayload)
{
    try { _ = step.AmbientOf(id, sealedPayload); return true; }
    catch (InvalidOperationException) { return false; }   // thrown once the key is gone
}
```

## Step 3 — Issue the erasure request

`ErasureCoordinator` runs a "forget subject S" request end to end: it finds every instance touching that
subject through the index, decides per instance whether to let it finish or force-terminate it, drives
the terminations to crypto-shred, and reports.

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

## Step 4 — See what survived and what didn't

```csharp
Console.WriteLine($"After erasure — key live: {keys.Has(instanceId)}");
Console.WriteLine($"After erasure — payload readable: {CanDecrypt(step, instanceId, sealedPayload)}");
Console.WriteLine($"Retained outward: {string.Join("; ", component.Retained)}");
```

## Run it

```sh
dotnet run
```

After the host startup logs, you should see:

```
Before erasure — key live: True
Before erasure — payload readable: True
Erased onboard-1: ForceTerminate → Complete
After erasure — key live: False
After erasure — payload readable: False
Retained outward: onboard-1/terminal/0: onboarding record (lawful basis: contract)
```

## What happened

The request named a subject rather than an instance. The coordinator looked the subject up in the
index, found `onboard-1`, and force-terminated it: `OnRetaining` fired first (writing the lawful-basis
record to your own store), then the per-instance key was destroyed and the subject pruned from the
index. After that, the sealed payload can never be decrypted again, because the key was the only way to
read it. This is crypto-shred: instead of hunting down and deleting every copy of the data, you destroy
the one key that makes it readable.

Note the division of labor. You decide what must be retained, and write it outward (PII-free) in
`OnRetaining`. SoEx destroys the key, prunes the index, and reports, the same way on every runtime.

## Next

- [How crypto-shred and erasure work](../explanation/crypto-shred-and-erasure.md) — the model, the
  guarantees, and the threat model behind what you just saw.
- [Run erasure maintenance](../how-to/run-erasure-maintenance.md) — the backstops that catch instances
  no one ever files a request for.
- [Make crypto-shred durable](../how-to/make-crypto-shred-durable.md) — swap the in-memory key store for
  one that survives a restart, so the shred holds in production.
