using Microsoft.AspNetCore.Builder;
using PiiMaker.Hosting;
using PiiMaker.Manager.Membership.Interface;
using SoEx.Abstractions;
using SoEx.Workflow;
using SoEx.Workflow.InMemory;

// =============================================================================================
// Example host: the InProc runtime, PORTABLE driver, as a web CONTROL PANEL. It stands up the
// "membership" SoEx system (the manager calls the Engine/Access components through the SoEx
// pipeline; see PiiMaker.Hosting.MembershipSystem), lifts the IMembershipEntry trigger seam into a
// generated HTTP controller, and serves a static button UI. A person drives the workflow by firing
// each awaited event from the browser instead of the old hardcoded script.
//
// Flows here: A onboarding (portable Onboard), B subscription renewal (portable Renew), D erasure.
// C offboarding is native-only (the portable flow can't fan out), so it stays unwired.
// Run: dotnet run --project examples/PiiMaker/Hosts/InProc -- [port]   (default 5001), then open http://localhost:5001
// =============================================================================================

const string subSystem = "membership";
int port = args is [var p, ..] && int.TryParse(p, out int n) ? n : 5001;

// The Workflow utility OWNS the durable governance stores; the host reads them back from the composed
// system to wire the governed step + termination (the per-step hot path can't go through a proxy).
MembershipSystem.Composition system = MembershipSystem.Compose(subSystem, MembershipPolicy.Default);
IInstanceKeyStore keys = system.Keys;
ISubjectIndex index = system.Index;
IIdempotencyStore idempotency = system.Idempotency;

GovernedStep<IMembershipManager> StepFor(string op) => new(system.Endpoint, system.Serializer, idempotency, keys, index, op);
var termination = new GovernedTermination(system.Erasure, keys, index, system.HeldLog);

// Wire the workflow seam: per flow, an InProc gateway + the seal-side of its governed operation. This is
// the ONLY runtime-specific wiring; it now stays alive for the process instead of driving a scripted run.
// The gateway authorization chokepoint is shown here in AllowAll mode (the panel buttons carry no token);
// swap in `new ExampleGatewayAuthorizer(resolveToken, isAuthorized)` to enforce a production policy on every start/raise-event.
IGatewayAuthorizer authorizer = ExampleGatewayAuthorizer.AllowAll;
var onboardGateway = new InProcWorkflowGateway<IMembershipManager>(StepFor(nameof(IMembershipManager.Onboard)), termination, authorizer);
var renewGateway = new InProcWorkflowGateway<IMembershipManager>(StepFor(nameof(IMembershipManager.Renew)), termination, authorizer);
system.Seam.Connect("onboard", onboardGateway, new WorkflowSealer(keys, system.Serializer, nameof(IMembershipManager.Onboard)), system.Serializer);
system.Seam.Connect("renew", renewGateway, new WorkflowSealer(keys, system.Serializer, nameof(IMembershipManager.Renew)), system.Serializer);

WebApplicationBuilder builder = MembershipWebHost.Create(port);
var capabilities = new MembershipWebHost.Capabilities(
    Runtime: "InProc", Onboarding: true, Renewal: true, Offboarding: false, Restart: false);
WebApplication app = MembershipWebHost.Build(builder, system, capabilities);

Console.WriteLine($"PiiMaker InProc control panel → http://localhost:{port}");
await app.RunAsync();
