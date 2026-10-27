using Docker.DotNet;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Tikai.Orchestrator;

public static class OrchestratorServiceCollectionExtensions
{
    /// <summary>
    /// The Docker client, the container runtime, the monitor, the reconciler, the cancel path and the collector of submitted work. The host binds the options and registers
    /// the start job (<see cref="RunStarter"/>, queue <see cref="RunJob.Queue"/>), an <see cref="IRunStore"/> and an adapter.
    /// </summary>
    public static IServiceCollection AddTikaiOrchestrator(this IServiceCollection services)
    {
        services.AddSingleton<IDockerClient>(sp =>
            new DockerClientBuilder().WithEndpoint(sp.GetRequiredService<IOptions<DockerOptions>>().Value.Endpoint).Build());
        services.AddSingleton<IContainerRuntime, DockerContainerRuntime>();
        services.AddSingleton<RunMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<RunMonitor>());
        services.AddHostedService<RunReconciler>();
        services.AddScoped<RunControl>();
        services.AddSingleton<RunWorkCollector>();
        return services;
    }
}
