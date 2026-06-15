> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Tutorial 1 — Build your first workflow

This tutorial builds a small onboarding workflow and runs it from start to finish, entirely in-process,
with no Docker, Temporal, or database required. The finished workflow runs a step, waits for an
external event, runs another step, and completes, with each step governed by SoEx and the whole
instance erasable by crypto-shred.

You don't need to understand every line yet. Follow the steps, run the program, and see it work. The
[how-to guides](../README.md#how-to-guides) and [explanations](../README.md#explanation) fill in the
"why" afterwards.

Plan on about 15 minutes. All you need is the .NET 10 SDK and a terminal.

## Set up the project

Create a console app and reference the packages. The `SoEx.Workflow*` packages are not yet on nuget.org
(see [Packages](../reference/packages.md)), so reference the built projects directly: clone this repo and
add a project reference, or build the assemblies and reference those. `SoEx.Hosting` and `SoEx.Context`
are the base-SoEx packages your composition root needs.

```sh
dotnet new console -n FirstWorkflow
cd FirstWorkflow
# from a clone of this repo (adjust the relative path):
dotnet add reference ../soex-workflow/src/SoEx.Workflow/SoEx.Workflow.csproj
dotnet add package SoEx.Hosting --prerelease
dotnet add package SoEx.Context --prerelease
```

`SoEx.Hosting` and `SoEx.Context` are published as pre-release (`0.0.0-alpha-3.0`), so `--prerelease` is
required — without it `dotnet add package` reports "no stable versions available".

Everything below goes in `Program.cs`. Replace its contents as you follow along. One thing to know about
the order: a C# file that uses top-level statements requires the `using` directives first, then the
executable statements, then any type declarations last. We introduce the types first because they're
easier to read that way, so when you assemble the file, collect every `using` line at the top, put the
Step 3–5 statements next, and move the Step 1–2 type declarations (`OnboardStep`, `IOnboardManager`,
`OnboardManager`) to the bottom.

## Step 1 — Model the steps

A workflow is a sequence of steps, and each step is a small DTO carrying just what that step needs.
There's no "next step" field; sequencing is handled elsewhere, so the DTO doesn't need to know about
it. A sealed hierarchy keeps things tidy:

```csharp
public abstract record OnboardStep
{
    public sealed record Lookup(string Email) : OnboardStep;
    public sealed record Invite(string Email, string ReservationId) : OnboardStep;
    public sealed record Assign(string ReservationId, string User) : OnboardStep;
}
```

## Step 2 — Write the component

Write one component whose step operation returns a [`WorkflowAction`](../reference/workflow-action.md)
telling SoEx what to do next. This is the portable model: you describe the flow with actions, and SoEx
runs it for you.

```csharp
using SoEx.Workflow;

public interface IOnboardManager
{
    Task<WorkflowAction> Run(OnboardStep step);
}

public sealed class OnboardManager : IOnboardManager, IErasureEvents
{
    public Task<WorkflowAction> Run(OnboardStep step) => Task.FromResult<WorkflowAction>(step switch
    {
        OnboardStep.Lookup l => new WorkflowAction.RaiseIntoNext(new OnboardStep.Invite(l.Email, "res-1")),
        OnboardStep.Invite  => new WorkflowAction.WaitForEvent("invite-accepted"),
        OnboardStep.Assign  => new WorkflowAction.Complete("assigned"),
        _ => throw new ArgumentOutOfRangeException(nameof(step)),
    });

    // The erasure events are required (see Tutorial 2). No-ops are fine for now.
    public Task OnRetaining(RetainingContext c) => Task.CompletedTask;
    public Task OnTerminated(TerminatedContext c) => Task.CompletedTask;
    public Task OnRetentionHeld(RetentionHeldContext c) => Task.CompletedTask;
}
```

That's the whole flow: look up the invitee, send an invite, wait for them to accept, then assign them
and complete.

## Step 3 — Wire the governed core

SoEx hosts your component behind an ordinary SoEx binding and gives you two handles: a governed step
(`step`) and a governed termination (`termination`). Copy this block as-is; the
[governed core reference](../reference/governed-core.md) explains each line.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SoEx.Abstractions;
using SoEx.Context;
using SoEx.Hosting;
using SoEx.Workflow;
using SoEx.Workflow.InMemory;

IInstanceKeyStore keys  = new InMemoryInstanceKeyStore();   // mints + destroys the per-instance key
ISubjectIndex     index = new InMemorySubjectIndex();        // maps subjects → instances for erasure
IIdempotencyStore idem  = new InMemoryIdempotencyStore();    // collapses at-least-once redelivery
var component = new OnboardManager();

var listeners = new WorkflowListeners();
var binding   = new WorkflowBinding<IOnboardManager>("onboarding");
var services  = new ServiceCollection();
services.AddSingleton(listeners);
services.AddSingleton<IContextFlowPolicy, SubjectContextFlowPolicy>();

var topology = new SoEx.Topology.HostMock   // qualified — `Host` below is Microsoft's host builder
{
    Instance = component, Implementation = component.GetType(),
    Endpoints = [binding], Proxies = [], ServiceCollection = services,
};
var builder = Host.CreateApplicationBuilder();
builder.SoEx(topology);
IHost host = builder.Build();
host.Start();

IWorkflowDispatch endpoint = listeners.ForAddress(binding.Transport.Address);
var serializer = host.Services.GetRequiredService<IMessageSerializer>();

WorkflowRegistration.RequireErasureEvents(component.GetType());   // fail fast if you forgot the contracts
var step     = new GovernedStep<IOnboardManager>(endpoint, serializer, idem, keys, index);
var termination = new GovernedTermination(component, keys, index);
```

## Step 4 — Seal the first step and run it

Starting a portable workflow means sealing the first step into a seed. Sealing mints the per-instance
key and encrypts the payload under it. Then you run the driver on the in-process runtime:

```csharp
using System.Text;

string instanceId = "onboard-1";

// Attach the subject (the person being onboarded) so SoEx can index + erase them later.
byte[] ambient = WorkflowEnvelope.AmbientFor(step.Serializer,
    SubjectContext.Managed("invitee@example.com"))!;

byte[] seed = step.SealStep(instanceId, new OnboardStep.Lookup("invitee@example.com"), ambient);

var runtime = new InMemoryWorkflowRuntime(instanceId);
var driver  = new WorkflowDriver<IOnboardManager>(runtime, step, termination);

Task<byte[]> completion = driver.RunAsync(seed);   // runs Lookup → Invite, then parks on the wait
Console.WriteLine("Workflow started; waiting for invite-accepted…");
```

## Step 5 — Raise the event and finish

The flow is now parked on `WaitForEvent("invite-accepted")`. Raise that event (the payload you raise
becomes the next step) and await completion:

```csharp
await runtime.RaiseEventAsync(instanceId, "invite-accepted",
    step.SealStep(instanceId, new OnboardStep.Assign("res-1", "confirmed-user")));

byte[] result = await completion;   // Assign → Complete("assigned")
Console.WriteLine($"Workflow completed: {Encoding.UTF8.GetString(result)}");
Console.WriteLine("The per-instance key was destroyed at the termination — the journal is now unrecoverable.");
```

## Run it

```sh
dotnet run
```

After a few host startup log lines (`Application started…`), you should see:

```
Workflow started; waiting for invite-accepted…
Workflow completed: "assigned"
The per-instance key was destroyed at the termination — the journal is now unrecoverable.
```

## What you built

You wrote one component, and SoEx ran it as a durable workflow: a step, a wait for an external event,
another step, and a clean completion. Along the way every step ran under a per-instance encryption key,
the subject (`invitee@example.com`) was indexed, and at the end that key was destroyed, so anything the
instance persisted is gone for good.

Notice that you never wrote any encryption code; in the portable model, SoEx seals everything it
journals for you. Notice also that the result you returned (`"assigned"`) is PII-free. Results are
journaled in clear, so they must not carry a subject. Anything you must keep gets written outward
instead, which is what Tutorial 2 does.

## Next

- [**Tutorial 2 — Erase a subject**](02-erase-a-subject.md): issue a "forget this person" request and
  watch crypto-shred in action.
- [Run the portable flow on a durable runtime](../how-to/run-the-portable-flow.md) — the same component
  on Temporal, Durable Task, Elsa, or Restate.
- [How the portable model works](../explanation/consumption-models.md).
