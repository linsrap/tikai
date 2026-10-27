using Tikai.Adapters.ClaudeCode;
using Tikai.Core.Agents;
using Tikai.Data;
using Tikai.Orchestrator;

namespace Tikai.Api.Runs;

/// <summary>Agent runs: the orchestrator with the Claude Code adapter, started through the runs job queue.</summary>
internal static class RunsSetup
{
    public static IServiceCollection AddTikaiRuns(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OrchestratorOptions>()
            .Bind(configuration.GetSection(OrchestratorOptions.Section))
            .Validate(o => !string.IsNullOrWhiteSpace(o.RunsVolume) && !string.IsNullOrWhiteSpace(o.Network), "Tikai:Agents:RunsVolume and Network must be set.")
            .Validate(o => Uri.TryCreate(o.McpUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https", "Tikai:Agents:McpUrl must be an http(s) URL.")
            .Validate(o => o.AgentUid > 0 && o.MemoryBytes > 0 && o.Cpus > 0 && o.PidsLimit > 0 && o.MaxEventsPerRun > 0,
                "Agent limits must be positive (and the agent uid must not be root).")
            .Validate(o => o.ToolCallTimeout >= TimeSpan.FromSeconds(1) && o.InitTimeout > TimeSpan.Zero && o.PrepareTimeout > TimeSpan.Zero,
                "Agent timeouts must be positive (the tool call timeout at least 1 s).")
            .ValidateOnStart();
        services.AddOptions<DockerOptions>().Bind(configuration.GetSection(DockerOptions.Section));
        services.AddOptions<ClaudeCodeOptions>().Bind(configuration.GetSection(ClaudeCodeOptions.Section));

        services.AddTikaiOrchestrator();
        services.AddSingleton<IAgentAdapter, ClaudeCodeAdapter>();
        services.AddScoped<IRunStore, RunStore>();
        services.AddJobHandler<RunStarter>(RunJob.Queue);
        return services;
    }
}
