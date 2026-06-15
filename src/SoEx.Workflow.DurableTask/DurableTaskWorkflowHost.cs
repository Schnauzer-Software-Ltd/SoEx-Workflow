using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.AzureManaged;
using Microsoft.DurableTask.Worker;
using Microsoft.DurableTask.Worker.AzureManaged;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SoEx.Workflow;

namespace SoEx.Workflow.DurableTask;

/// <summary>
/// Portable flow — hosts the workflow on the modern Durable Task SDK against a Durable Task
/// Scheduler endpoint (the Azure service in production, or its local emulator in dev/test). The governed
/// step + termination are wired into the DI container so the activities resolve them. Use this or the
/// native flow, never both in one host.
/// </summary>
public static class DurableTaskWorkflowHost
{
    public const string OrchestrationName = nameof(WorkflowOrchestration);

    public static IHost Build(string connectionString, IGovernedStep step, GovernedTermination termination)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();

        builder.Services.AddSingleton(step);
        builder.Services.AddSingleton(termination);

        builder.Services.AddDurableTaskWorker(worker =>
        {
            worker.AddTasks(tasks =>
            {
                tasks.AddOrchestrator<WorkflowOrchestration>();
                tasks.AddActivity<StepActivity>();
                tasks.AddActivity<TerminateActivity>();
            });
            worker.UseDurableTaskScheduler(connectionString);
        });

        builder.Services.AddDurableTaskClient(client => client.UseDurableTaskScheduler(connectionString));

        return builder.Build();
    }
}
