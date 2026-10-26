using Microsoft.Extensions.DependencyInjection;
using Tikai.Core.Git;
using Tikai.Git.Processes;

namespace Tikai.Git;

public static class GitServiceCollectionExtensions
{
    /// <summary>Registers <see cref="IGitService"/>. Bind <see cref="GitOptions"/> in the host.</summary>
    public static IServiceCollection AddTikaiGit(this IServiceCollection services)
    {
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IGitService, GitService>();
        return services;
    }
}
