> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Tutorial 1: Build your first workflow

In this tutorial, you build a small onboarding workflow and run it from start to end. The workflow runs
in-process. It needs no Docker, no Temporal, and no database. The finished workflow does these
operations in sequence:

1. It runs a step.
2. It waits for an external event.
3. It runs one more step.
4. It completes.

SoEx governs each step. Crypto-shred can erase the full instance.

Follow the steps, run the program, and see the result. You can learn the reasons later, from the
[how-to guides](../README.md#how-to-guides) and the [explanations](../README.md#explanation).

The tutorial takes approximately 15 minutes. You need the .NET 10 SDK and a terminal.

## Set up the project

The `SoEx.Workflow*` packages are not on nuget.org at this time (see
[Packages](../reference/packages.md)). Thus you reference the built projects. You can clone this repo
and add project references, or you can build the assemblies and reference them. `SoEx.Hosting` and
`SoEx.Context` are the base-SoEx packages that your composition root needs.

1. Make a console app.
2. Add references to the `SoEx.Workflow*` projects from a clone of this repo.
3. Add the `SoEx.Hosting` and `SoEx.Context` packages with `--prerelease`.

```sh
dotnet new console -n FirstWorkflow
cd FirstWorkflow
# from a clone of this repo (adjust the relative path):
dotnet add reference ../soex-workflow/src/SoEx.Workflow/SoEx.Workflow.csproj
dotnet add reference ../soex-workflow/src/SoEx.Transport.Workflow/SoEx.Transport.Workflow.csproj
dotnet add reference ../soex-workflow/src/SoEx.Workflow.Runtime.InMemory/SoEx.Workflow.Runtime.InMemory.csproj
dotnet add package SoEx.Hosting --prerelease
dotnet add package SoEx.Context --prerelease
```

`SoEx.Hosting` and `SoEx.Context` are pre-release packages, so `--prerelease` is mandatory. Without it,
`dotnet add package` reports "no stable versions available". This repo builds with version
`0.0.0-alpha-4.1`. If `--prerelease` selects a different version, pin the correct version with
`--version 0.0.0-alpha-4.1`.

All the code in this tutorial goes in `Program.cs`. Replace the contents of that file as you go.

A C# file with top-level statements must have this order:

1. the `using` directives
2. the executable statements
3. the type declarations

This tutorial shows the types first, because they are easier to read in that order. When you assemble
the file, do these steps:

1. Put all the `using` lines at the top.
2. Put the statements from Steps 3–5 next.
3. Move the type declarations from Steps 1–2 to the bottom: `OnboardStep`, `IOnboardManager`, and
   `OnboardManager`.

## Step 1: Model the steps

A workflow is a sequence of steps. Each step is a small DTO that holds only the data for that step. The
driver sets the order of the steps, so the DTO has no field for the next step.

1. Write the steps as a sealed hierarchy of records.

```csharp
public abstract record OnboardStep
{
    public sealed record Lookup(string Email) : OnboardStep;
    public sealed record Invite(string Email, string ReservationId) : OnboardStep;
    public sealed record Assign(string ReservationId, string User) : OnboardStep;
}
```

## Step 2: Write the component

This tutorial uses the portable flow. In the portable flow, your step operation returns a
[`WorkflowAction`](../reference/workflow-action.md). The action tells SoEx what to do next. SoEx runs
the flow that the actions describe.

1. Write one component with a step operation that returns a `WorkflowAction`.
2. Add the three erasure events as empty operations.

   Tutorial 2 uses these events.

```csharp
using SoEx.Workflow;

public interface IOnboardManager
{
    Task<WorkflowAction> Run(OnboardStep step);
}

public sealed class OnboardManager : IOnboardManager, IErasureEvent
{
    public Task<WorkflowAction> Run(OnboardStep step) => Task.FromResult<WorkflowAction>(step switch
    {
        OnboardStep.Lookup l => new WorkflowAction.RaiseIntoNext(new OnboardStep.Invite(l.Email, "res-1")),
        OnboardStep.Invite  => new WorkflowAction.WaitForEvent([new EventBranch("invite-accepted")]),
        OnboardStep.Assign  => new WorkflowAction.Complete("assigned"),
        _ => throw new ArgumentOutOfRangeException(nameof(step)),
    });

    // The erasure events are required (see Tutorial 2). No-ops are fine for now.
    public Task OnRetaining(RetainingContext c) => Task.CompletedTask;
    public Task OnTerminated(TerminatedContext c) => Task.CompletedTask;
    public Task OnRetentionHeld(RetentionHeldContext c) => Task.CompletedTask;
}
```

This code is the full flow:

1. Look up the invitee.
2. Send an invite.
3. Wait for the invitee to accept.
4. Assign the invitee and complete.

## Step 3: Wire the governed core

SoEx hosts your component behind a usual SoEx binding. It gives you two handles: a governed step
(`step`) and a governed termination (`termination`).

1. Copy this block with no changes.

   The [governed core reference](../reference/governed-core.md) explains each line.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SoEx.Abstractions;
using SoEx.Context;
using SoEx.Hosting;
using SoEx.Transport.Workflow;                              // WorkflowBinding / WorkflowListeners
using SoEx.Workflow;
using SoEx.Workflow.Runtime.InMemory;

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
// The host's serializer binds every value to its declared type. The subject and your steps travel in
// slots declared `object`, so name them up front.
var knownTypes = new KnownTypes([
    .. WorkflowKnownTypes.Framework,
    typeof(OnboardStep.Lookup), typeof(OnboardStep.Invite), typeof(OnboardStep.Assign),
]);

var builder = Host.CreateApplicationBuilder();
builder.SoEx(topology, knownTypes);
IHost host = builder.Build();
host.Start();

IWorkflowDispatch endpoint = listeners.ForAddress(binding.Transport.Address.Uri);
var serializer = host.Services.GetRequiredService<IMessageSerializer>();

WorkflowRegistration.RequireErasureEvent(component.GetType());   // fail fast if you forgot the contracts
var step     = new GovernedStep<IOnboardManager>(endpoint, serializer, idem, keys, index);
var termination = new GovernedTermination(component, keys, index);
```

## Step 4: Seal the first step and run it

A portable workflow starts from a seed. The seed is the sealed first step. The seal operation makes the
per-instance key and encrypts the payload with that key.

1. Attach the subject. The subject is the person that the workflow onboards.

   SoEx uses the subject to index the person and to erase the person later.
2. Seal the first step into a seed.
3. Run the driver on the in-process runtime.

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

## Step 5: Raise the event and finish

The flow now waits on its `invite-accepted` branch. This branch has no `OnEvent`. Thus the raiser
supplies the next step: the payload that you raise becomes the step. If a branch has an `OnEvent`, the
flow selects the next step. The data that the raise carries then arrives as
[event data](../reference/workflow-action.md#receiving-data-with-an-event).

1. Raise the `invite-accepted` event with the `Assign` step as its payload.
2. Await the completion.

```csharp
await runtime.RaiseEventAsync(instanceId, "invite-accepted",
    step.SealStep(instanceId, new OnboardStep.Assign("res-1", "confirmed-user")));

byte[] result = await completion;   // Assign → Complete("assigned")
Console.WriteLine($"Workflow completed: {Encoding.UTF8.GetString(result)}");
Console.WriteLine("The per-instance key was destroyed at the termination — the journal is now unrecoverable.");
```

## Run it

1. Run the program.

```sh
dotnet run
```

The host first writes some startup log lines (`Application started…`). Then you see this output:

```
Workflow started; waiting for invite-accepted…
Workflow completed: "assigned"
The per-instance key was destroyed at the termination — the journal is now unrecoverable.
```

## What you built

You wrote one component, and SoEx ran it as a workflow on InProc. The same component runs with no
change on the durable runtimes. The workflow ran a step, waited for an external event, ran one more
step, and completed. During the run, these things occurred:

- Each step ran with a per-instance encryption key.
- SoEx indexed the subject (`invitee@example.com`).
- At the end, SoEx destroyed the key. All data that the instance kept is now unrecoverable.

You wrote no encryption code. In the portable flow, SoEx seals all the data that it writes to the
journal. The result that you returned (`"assigned"`) contains no PII (personally identifiable
information). SoEx writes results to the journal in clear text, so a result must not contain a subject.
Write the data that you must keep to your own store. Tutorial 2 shows how.

## Next

- [**Tutorial 2: Erase a subject**](02-erase-a-subject.md). Send a request to forget a person, and see
  crypto-shred erase the data.
- [Run the portable flow on a durable runtime](../how-to/run-the-portable-flow.md). Run the same
  component on Temporal, Durable Task, Elsa, or Restate.
- [How the portable model works](../explanation/consumption-models.md).
