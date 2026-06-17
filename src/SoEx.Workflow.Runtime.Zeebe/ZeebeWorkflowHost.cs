using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SoEx.Workflow;
using Zeebe.Client;
using Zeebe.Client.Api.Builder;
using Zeebe.Client.Api.Responses;
using Zeebe.Client.Api.Worker;

namespace SoEx.Workflow.Runtime.Zeebe;

/// <summary>
/// A BPMN-lint finding from <see cref="ZeebeWorkflowHost.ValidateResource"/>: a governed task whose
/// io-mapping copies a framework-owned variable into the journal under a name the framework can't guarantee.
/// </summary>
public sealed record ZeebeResourceWarning(string ProcessId, string ElementId, string Detail);

/// <summary>
/// Native flow on Camunda 8 / Zeebe — the .NET job-worker side of a BPMN graph the broker owns. The flow
/// is authored in a visual editor (BPMN-js / Camunda Modeler); each governed service task is a job whose
/// worker runs one <see cref="GovernedStep{I}"/>, and a process end-execution-listener job runs the
/// <see cref="GovernedTermination"/> crypto-shred when the instance ends. The broker journals process
/// variables, so the only step payload it ever sees is the sealed seed (ciphertext) — destroying the
/// instance key at the termination renders it unrecoverable.
/// <para>
/// These are the runtime mechanics only (connect · deploy · open workers); the mapping from a PII-free
/// step "kind" to a typed command is the consumer's, supplied as the <c>run</c> delegate — exactly as the
/// Temporal adapter keeps generic activities separate from the consumer's per-step logic.
/// </para>
/// </summary>
public static class ZeebeWorkflowHost
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The reserved process variables the framework owns: the PII-free logical instance id and the
    /// sealed (ciphertext) seed the flow threads. A consumer's own variables live alongside these.</summary>
    internal sealed class FrameworkVars
    {
        [JsonPropertyName("instanceId")] public string? InstanceId { get; set; }
        [JsonPropertyName("seed")] public string? Seed { get; set; }
    }

    /// <summary>
    /// Opens a <b>plaintext</b> gRPC client to a Zeebe gateway (e.g. <c>127.0.0.1:26500</c>). The sealed seed
    /// and the PII-free instance id are the only payload that crosses this connection, but they cross in clear
    /// along with any gateway credentials — so this is for a loopback/dev gateway. For a gateway reached over a
    /// network use <see cref="ConnectSecure"/> (TLS, optional access token), or build your own
    /// <see cref="IZeebeClient"/> and pass it to <see cref="DeployAsync"/>/<see cref="OpenStepWorker"/>/
    /// <see cref="OpenTerminationListener"/>, which all accept a pre-built client.
    /// </summary>
    public static IZeebeClient Connect(string gatewayAddress) =>
        ZeebeClient.Builder().UseGatewayAddress(gatewayAddress).UsePlainText().Build();

    /// <summary>
    /// Opens a <b>TLS</b> gRPC client to a Zeebe gateway. Pass <paramref name="rootCertificatePath"/> (a PEM
    /// file) to pin a private CA; omit it to verify against the OS trust store. Supply
    /// <paramref name="accessToken"/> for a gateway that requires bearer auth (e.g. Camunda 8 SaaS or an
    /// authenticated self-managed gateway). <paramref name="allowUntrustedCertificates"/> disables certificate
    /// validation and is for test environments only — never enable it against a real gateway.
    /// </summary>
    public static IZeebeClient ConnectSecure(
        string gatewayAddress,
        string? rootCertificatePath = null,
        string? accessToken = null,
        bool allowUntrustedCertificates = false)
    {
        IZeebeClientTransportBuilder transport = ZeebeClient.Builder().UseGatewayAddress(gatewayAddress);
        IZeebeSecureClientBuilder secure = string.IsNullOrEmpty(rootCertificatePath)
            ? transport.UseTransportEncryption()
            : transport.UseTransportEncryption(rootCertificatePath);

        if (allowUntrustedCertificates)
        {
            secure = secure.AllowUntrustedCertificates();
        }

        return string.IsNullOrEmpty(accessToken) ? secure.Build() : secure.UseAccessToken(accessToken).Build();
    }

    /// <summary>
    /// Deploys one or more BPMN resources (the visual-editor artifacts) to the broker, and returns any
    /// io-mapping lint warnings for the deployed resources. The warnings are <b>advisory</b> — a governed
    /// service task whose output io-mapping copies a framework-owned variable into the journal under a name
    /// the framework does not control. The host does not log, so they are surfaced to the caller, which
    /// decides whether to act (or block) on them; <see cref="ValidateResource"/> previously was reachable but
    /// never run on the deploy path. Deployment still proceeds regardless.
    /// </summary>
    public static async Task<IReadOnlyList<ZeebeResourceWarning>> DeployAsync(IZeebeClient client, params string[] bpmnResourcePaths)
    {
        if (bpmnResourcePaths is not { Length: > 0 })
        {
            throw new ArgumentException("at least one BPMN resource path is required", nameof(bpmnResourcePaths));
        }

        var warnings = new List<ZeebeResourceWarning>();
        foreach (string path in bpmnResourcePaths)
        {
            warnings.AddRange(ValidateResource(path));
        }

        var step = client.NewDeployCommand().AddResourceFile(bpmnResourcePaths[0]);
        for (int i = 1; i < bpmnResourcePaths.Length; i++)
        {
            step = step.AddResourceFile(bpmnResourcePaths[i]);
        }

        await step.Send();
        return warnings;
    }

    /// <summary>
    /// Opens a worker for a governed service-task job type. Per job: recovers the logical instance id and
    /// the sealed seed from the process variables, the required PII-free <c>kind</c> from the task headers,
    /// keys idempotency on the job's element-instance key (unique per loop iteration, stable across redelivery),
    /// guards the in-clear instance id + the journaled step receipt against carrying a subject id, runs the
    /// consumer's <paramref name="run"/> step, and completes the job. A throwing step fails the job (a broker
    /// incident) with a PII-free message rather than silently dropping the effect.
    /// </summary>
    public static IJobWorker OpenStepWorker(
        IZeebeClient client,
        string jobType,
        IGovernedStep step,
        Func<string, long, string, byte[], Task<object?>> run,
        string? workerName = null) =>
        client.NewWorker()
            .JobType(jobType)
            .Handler(async (jobClient, job) =>
            {
                string? failure = await ExecuteStepJob(
                    step, job.Variables, job.CustomHeaders, job.ElementInstanceKey, Describe(job), run);
                await Settle(jobClient, job, failure);
            })
            .MaxJobsActive(5)
            .Name(workerName ?? jobType)
            .Timeout(TimeSpan.FromSeconds(30))
            .PollInterval(TimeSpan.FromMilliseconds(100))
            .Open();

    /// <summary>
    /// Opens a worker for the process <b>end execution-listener</b> job type — the native-flow termination hook.
    /// When the instance ends the broker raises this listener job; the worker guards the in-clear instance id,
    /// runs the erasure lifecycle (crypto-shred), and completes the listener so the element can finish. A
    /// failure raises an incident (PII-free message) rather than letting the instance end without the shred.
    /// <para>
    /// <b>Cancellation.</b> A broker-side <c>CancelProcessInstance</c> terminates the instance without running
    /// end execution-listeners, so this hook does <em>not</em> fire on an administrative cancel — exactly like
    /// management terminate/purge on the other engines. That path is closed by the request-independent
    /// <c>ErasureCoordinator.SweepAsync</c> (and any later erasure request), which ages and shreds the live key
    /// set, so the sweep is load-bearing here rather than optional. The trigger recorded is
    /// <c>NaturalCompletion</c> because the listener only runs on a normal end.
    /// </para>
    /// </summary>
    public static IJobWorker OpenTerminationListener(
        IZeebeClient client,
        string listenerJobType,
        IGovernedStep step,
        GovernedTermination termination,
        string? workerName = null) =>
        client.NewWorker()
            .JobType(listenerJobType)
            .Handler(async (jobClient, job) =>
            {
                string? failure = await ExecuteTerminationJob(step, termination, job.Variables, Describe(job));
                await Settle(jobClient, job, failure);
            })
            .MaxJobsActive(5)
            .Name(workerName ?? listenerJobType)
            .Timeout(TimeSpan.FromSeconds(30))
            .PollInterval(TimeSpan.FromMilliseconds(100))
            .Open();

    /// <summary>
    /// Runs one governed service-task job from its raw process variables + custom headers (the JSON shapes the
    /// broker hands a worker) and the job's element-instance key. Returns <c>null</c> when the job should be
    /// completed, or a PII-free incident message when it should be failed. Broker-free so the guard chokepoint
    /// and the idempotency keying are unit-testable.
    /// </summary>
    internal static async Task<string?> ExecuteStepJob(
        IGovernedStep step, string variablesJson, string headersJson, long elementInstanceKey, string jobDesc,
        Func<string, long, string, byte[], Task<object?>> run)
    {
        byte[]? ambient = null;
        try
        {
            FrameworkVars vars = JsonSerializer.Deserialize<FrameworkVars>(variablesJson, Json) ?? new();
            string instanceId = Require(vars.InstanceId, "instanceId", jobDesc);
            byte[] seed = Convert.FromBase64String(Require(vars.Seed, "seed", jobDesc));
            string kind = RequireHeader(headersJson, "kind", jobDesc);
            ambient = step.AmbientOf(instanceId, seed);

            // The instance id is journaled in clear (the gateway names it at start; the broker keeps it as a
            // process variable + message correlation key), so reject it before the step runs if it carries a
            // known subject id — the chokepoint every adapter applies, since the broker can't intercept start.
            step.GuardVisibleName(instanceId, ambient);

            // The idempotency sequence is the job's element-instance key, NOT a static task header: it is unique
            // per loop iteration / multi-instance body yet stable across a job redelivery, so each distinct
            // iteration applies its effect once while a retried iteration dedupes on the same key. A static
            // header would make every iteration collide on (instanceId, DtoType, seq) and silently absorb
            // iterations 2+. Zeebe keys are globally unique, so this also separates continue-as-new generations.
            object? result = await run(instanceId, elementInstanceKey, kind, seed);

            // The step receipt is journaled in clear (escapes the shred), so enforce it is PII-free — the same
            // guard every adapter applies to a returned result. The receipt is not written back: it carries no
            // routing, and the journal stays to instance-id + sealed seed only.
            step.GuardResultPiiFree(step.Serializer.Serialize(result), ambient);
            return null;
        }
        catch (Exception ex)
        {
            return SafeIncidentMessage(ex.Message, step, ambient);
        }
    }

    /// <summary>
    /// Runs the termination (crypto-shred) execution-listener job from its raw process variables. Guards the
    /// in-clear instance id while the key is still live, then shreds. Returns <c>null</c> to complete, or a
    /// PII-free incident message to fail. Broker-free so the guard chokepoint is unit-testable.
    /// </summary>
    internal static async Task<string?> ExecuteTerminationJob(
        IGovernedStep step, GovernedTermination termination, string variablesJson, string jobDesc)
    {
        byte[]? ambient = null;
        try
        {
            FrameworkVars vars = JsonSerializer.Deserialize<FrameworkVars>(variablesJson, Json) ?? new();
            string instanceId = Require(vars.InstanceId, "instanceId", jobDesc);

            // The sealed seed rides as a reserved process variable for the instance's life, so the subjects are
            // still derivable here (the key is destroyed only by the shred below) — guard the in-clear instance
            // id at the termination seam too, matching the step worker.
            if (vars.Seed is { } encoded)
            {
                ambient = step.AmbientOf(instanceId, Convert.FromBase64String(encoded));
            }

            step.GuardVisibleName(instanceId, ambient);

            await termination.TerminateAsync(
                instanceId,
                new IdempotencyKey(instanceId, "terminal", 0),
                TerminationTrigger.NaturalCompletion);
            return null;
        }
        catch (Exception ex)
        {
            return SafeIncidentMessage(ex.Message, step, ambient);
        }
    }

    /// <summary>Completes the job on success, or fails it (a broker incident) with the PII-free message.</summary>
    private static async Task Settle(IJobClient jobClient, IJob job, string? failure)
    {
        if (failure is null)
        {
            await jobClient.NewCompleteJobCommand(job.Key).Send();
        }
        else
        {
            await jobClient.NewFailCommand(job.Key).Retries(0).ErrorMessage(failure).Send();
        }
    }

    /// <summary>
    /// A PII-free message for a failed-job incident. The broker records the failure message in clear in the
    /// incident (visible in Operate) and it survives the shred, so a raw exception message that carries a
    /// known subject id is replaced with a fixed string; a message free of every known subject passes through
    /// for diagnosability — the same substring guard the framework applies to every other in-clear name.
    /// </summary>
    private static string SafeIncidentMessage(string? raw, IGovernedStep step, byte[]? ambient)
    {
        try
        {
            return step.GuardVisibleName(raw ?? "governed job failed", ambient);
        }
        catch
        {
            return "governed job failed; detail withheld to keep the incident PII-free";
        }
    }

    /// <summary>Reads a required custom task header, throwing (→ a PII-free incident) if it is missing or empty
    /// rather than silently defaulting — a typo'd <c>kind</c> would otherwise route to the wrong step.</summary>
    private static string RequireHeader(string headersJson, string name, string jobDesc)
    {
        Dictionary<string, string> headers =
            JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson, Json) ?? new();
        return headers.TryGetValue(name, out string? value) && !string.IsNullOrEmpty(value)
            ? value
            : throw new InvalidOperationException($"{jobDesc} is missing the required '{name}' task header");
    }

    private static readonly XNamespace Bpmn = "http://www.omg.org/spec/BPMN/20100524/MODEL";
    private static readonly XNamespace Zeebe = "http://camunda.org/schema/zeebe/1.0";

    /// <summary>The framework-owned process variables (see <see cref="FrameworkVars"/>). An io-mapping that
    /// reads either of these into another journaled variable widens the footprint the framework owns.</summary>
    private static readonly Regex ReservedVarReference = new(@"\b(seed|instanceId)\b", RegexOptions.Compiled);

    /// <summary>
    /// Lints a BPMN resource before deploy: the framework guarantees its <i>own</i> journaled footprint is
    /// ciphertext-only (the sealed seed + the guarded instance id), but a consumer's BPMN io-mappings can
    /// journal anything. This flags the common mistake — a governed service task whose <c>zeebe:ioMapping</c>
    /// output copies the framework-owned <c>seed</c>/<c>instanceId</c> variable into another process variable,
    /// which the broker journals (visible in Operate) under a name outside the framework's control. Returns one
    /// <see cref="ZeebeResourceWarning"/> per offending mapping; an empty list means the shipped footprint
    /// stands. Consumer-authored variables unrelated to the framework's are the consumer's own concern.
    /// </summary>
    public static IReadOnlyList<ZeebeResourceWarning> ValidateResource(string bpmnResourcePath)
    {
        XDocument doc = XDocument.Load(bpmnResourcePath);
        var warnings = new List<ZeebeResourceWarning>();

        foreach (XElement process in doc.Descendants(Bpmn + "process"))
        {
            string processId = process.Attribute("id")?.Value ?? "(unnamed process)";

            // Only governed jobs matter: a service task with a zeebe:taskDefinition runs a GovernedStep, so its
            // io-mapping is in the framework's path. Plain tasks without one are the consumer's own.
            foreach (XElement task in process.Descendants(Bpmn + "serviceTask"))
            {
                if (task.Descendants(Zeebe + "taskDefinition").FirstOrDefault() is null)
                {
                    continue;
                }

                string elementId = task.Attribute("id")?.Value ?? "(unnamed task)";
                foreach (XElement output in task.Descendants(Zeebe + "output"))
                {
                    string source = output.Attribute("source")?.Value ?? "";
                    string target = output.Attribute("target")?.Value ?? "(unknown)";
                    if (ReservedVarReference.IsMatch(source))
                    {
                        warnings.Add(new ZeebeResourceWarning(processId, elementId,
                            $"output io-mapping '{source}' -> '{target}' copies a framework-owned variable into the " +
                            "journal under a name the framework does not control; remove it to keep the broker " +
                            "footprint to the sealed seed + guarded instance id only"));
                    }
                }
            }
        }

        return warnings;
    }

    private static string Describe(IJob job) => $"job {job.Key} ({job.Type})";

    private static string Require(string? value, string name, string jobDesc) =>
        value ?? throw new InvalidOperationException(
            $"{jobDesc} is missing the required '{name}' process variable");
}
