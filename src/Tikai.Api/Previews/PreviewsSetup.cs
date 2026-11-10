using Tikai.Data;
using Tikai.Orchestrator.Previews;

namespace Tikai.Api.Previews;

/// <summary>Previews of review rounds: settings, the start and teardown jobs, the log follower, the reconciler and the proxy's upstream cache.</summary>
internal static class PreviewsSetup
{
    public static IServiceCollection AddTikaiPreviews(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PreviewOptions>()
            .Bind(configuration.GetSection(PreviewOptions.Section))
            .Validate(o => !string.IsNullOrWhiteSpace(o.PreviewsVolume), "Tikai:Previews:PreviewsVolume must be set.")
            .Validate(o => o.MaxRunning > 0 && o.MemoryBytes > 0 && o.Cpus > 0 && o.PidsLimit > 0 && o.MaxLogLines > 0,
                "Preview limits must be positive.")
            .Validate(o => o.MaxLifetime > TimeSpan.Zero && o.PullTimeout > TimeSpan.Zero && o.SetupTimeout > TimeSpan.Zero && o.StartTimeout > TimeSpan.Zero,
                "Preview timeouts must be positive.")
            .ValidateOnStart();
        services.AddScoped<IPreviewStore, PreviewStore>();
        services.AddScoped<PreviewService>();
        services.AddSingleton<PreviewLogFollower>();
        services.AddJobHandler<PreviewStarter>(PreviewJob.StartQueue);
        services.AddJobHandler<PreviewTeardown>(PreviewJob.TeardownQueue);
        services.AddHostedService<PreviewReconciler>();
        services.AddSingleton<PreviewUpstreamCache>();
        services.AddHostedService(sp => sp.GetRequiredService<PreviewUpstreamCache>());
        services.AddHttpForwarder();
        return services;
    }
}
