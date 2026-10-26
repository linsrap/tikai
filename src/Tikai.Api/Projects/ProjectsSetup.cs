using Tikai.Api.Hosting;
using Tikai.Data;
using Tikai.Git;

namespace Tikai.Api.Projects;

/// <summary>Settings of the repository sync (configuration section Tikai:Git, next to <see cref="GitOptions"/>).</summary>
public sealed class RepositorySyncOptions
{
    /// <summary>Time between fetches of one repository.</summary>
    public TimeSpan FetchInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How often the scheduler looks for repositories that are due.</summary>
    public TimeSpan SchedulerInterval { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>Where the bare repositories live: &lt;data&gt;/repos/&lt;project-id&gt;/&lt;repository-id&gt;.git. Built from ids only, never from names.</summary>
internal sealed class MirrorPaths(string root)
{
    public string Root { get; } = root;

    public string For(Guid projectId, Guid repositoryId) =>
        Path.Combine(Root, projectId.ToString("D"), repositoryId.ToString("D") + ".git");
}

internal static class ProjectsSetup
{
    public static IServiceCollection AddTikaiProjects(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<GitOptions>()
            .Bind(configuration.GetSection(GitOptions.Section))
            .Validate(o => o.AllowedProtocolList.Count > 0 && o.AllowedProtocolList.All(GitOptions.KnownProtocols.Contains),
                $"Tikai:Git:AllowedProtocols must list some of: {string.Join(", ", GitOptions.KnownProtocols)}.")
            .Validate(o => o.LocalTimeout > TimeSpan.Zero && o.RemoteTimeout > TimeSpan.Zero, "Git timeouts must be positive.")
            .ValidateOnStart();
        services.AddOptions<RepositorySyncOptions>()
            .Bind(configuration.GetSection(GitOptions.Section))
            .Validate(o => o.FetchInterval > TimeSpan.Zero && o.SchedulerInterval > TimeSpan.Zero, "Fetch intervals must be positive.")
            .ValidateOnStart();

        services.AddTikaiGit();
        services.AddSingleton(new MirrorPaths(Path.Combine(DataPaths.Resolve(configuration, environment), "repos")));
        services.AddSingleton<RepositoryCredentials>();
        services.AddScoped<RepositoryService>();
        services.AddJobHandler<GitJobHandler>(GitJob.Queue);
        services.AddHostedService<RepositoryFetchScheduler>();
        return services;
    }
}
