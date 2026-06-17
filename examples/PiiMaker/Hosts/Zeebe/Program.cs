using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using PiiMaker.Hosting;
using PiiMaker.Manager.Membership.Interface;
using Native = PiiMaker.Manager.Membership.Interface.Native;
using SoEx.Workflow;
using SoEx.Workflow.Runtime.InMemory;
using SoEx.Workflow.Runtime.Zeebe;
using Zeebe.Client;

// =============================================================================================
// Example host: Camunda 8 / Zeebe, NATIVE flow, as a web CONTROL PANEL. The onboarding flow is a BPMN
// graph authored in a visual editor (bpmn/membership-onboard.bpmn) and deployed to the broker at startup.
// The broker owns the flow; this process is the .NET job-worker side:
//   - each "soex-onboard-step" service task runs one governed OnboardStep (kind + sequence from task headers)
//   - the "invite-accepted" message-catch is resumed by the InviteAccepted trigger (a correlated message)
//   - a process end execution-listener job ("soex-terminal") runs the crypto-shred termination at completion
// In this example the broker journals only the sealed seed (ciphertext) + the PII-free instance id —
// visible in Operate. That is a property of this flow's variables, not an enforced adapter guarantee.
// Requires Camunda 8 Run (gateway :26500, Operate :8090). Run: ./c8run start -port 8090
// Run: dotnet run --project examples/PiiMaker/Hosts/Zeebe -- [port]  (default 5006), then open http://localhost:5006
// =============================================================================================

internal class Program
{
    static async Task Main(string[] args)
    {
        const string gateway = "127.0.0.1:26500";
        const string subSystem = "membership";
        int port = args is [var p, ..] && int.TryParse(p, out int n) ? n : 5006;

        if (!GatewayReachable())
        {
            Console.WriteLine("✗ cannot reach a Zeebe gateway at 127.0.0.1:26500; start Camunda 8 Run " +
                              "(./c8run start -port 8090) and retry.");
            return;
        }

        // The Workflow utility owns the durable governance stores; read them back to wire the governed step + termination.
        MembershipSystem.Composition system = MembershipSystem.Compose(subSystem, MembershipPolicy.Default);
        IInstanceKeyStore keys = system.Keys;
        ISubjectIndex index = system.Index;
        IIdempotencyStore idempotency = system.Idempotency;
        var termination = new GovernedTermination(system.Erasure, keys, index, system.HeldLog);
        var onboardStep = new GovernedStep<Native.IMembershipManager>(
            system.NativeEndpoint, system.Serializer, idempotency, keys, index, nameof(Native.IMembershipManager.Onboard));

        // Connect to the broker and deploy the BPMN graph (the visual-editor artifact) from the host output.
        IZeebeClient client = ZeebeWorkflowHost.Connect(gateway);
        await ZeebeWorkflowHost.DeployAsync(client, Path.Combine(AppContext.BaseDirectory, "bpmn", "membership-onboard.bpmn"));

        // Two job workers, alive for the whole run: the governed onboarding step (each BPMN service-task kind maps a
        // PII-free kind to the typed OnboardStep command via MembershipNative) and the end-of-process termination shred.
        using var stepWorker = ZeebeWorkflowHost.OpenStepWorker(client, "soex-onboard-step", onboardStep,
            async (instanceId, seq, kind, seed) => await MembershipNative.RunOnboardStep(onboardStep, instanceId, seq, kind, seed));
        using var terminationWorker = ZeebeWorkflowHost.OpenTerminationListener(client, "soex-terminal", onboardStep, termination);

        // Wire the workflow seam: onboarding starts by sealing the OnboardStep seed and creating the BPMN process
        // instance; InviteAccepted publishes the correlated "invite-accepted" message. The BPMN graph IS the
        // onboarding flow, so only onboarding is wired here (renewal/offboarding stay unwired on this host).
        system.Seam.Connect("onboard", new ZeebeWorkflowGateway(client, "membership-onboard"),
            new WorkflowSealer(keys, system.Serializer, nameof(Native.IMembershipManager.Onboard)), system.Serializer);

        WebApplicationBuilder builder = MembershipWebHost.Create(port);
        var capabilities = new MembershipWebHost.Capabilities(
            Runtime: "Zeebe", Onboarding: true, Renewal: false, Offboarding: false, Restart: false,
            Dashboard: MembershipWebHost.DashboardFromEnv());
        WebApplication app = MembershipWebHost.Build(builder, system, capabilities);

        Console.WriteLine($"PiiMaker Zeebe (Camunda 8) control panel → http://localhost:{port}  (gateway {gateway}, Operate http://localhost:8090)");
        await app.RunAsync();
        client.Dispose();

        static bool GatewayReachable()
        {
            try
            {
                using var tcp = new TcpClient();
                return tcp.ConnectAsync("127.0.0.1", 26500).Wait(TimeSpan.FromSeconds(2)) && tcp.Connected;
            }
            catch (SocketException)
            {
                return false;
            }
        }
    }
}
