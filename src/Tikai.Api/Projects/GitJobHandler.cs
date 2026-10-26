using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tikai.Core.Git;
using Tikai.Core.Jobs;
using Tikai.Core.Projects;
using Tikai.Data;

namespace Tikai.Api.Projects;

/// <summary>A job on the git queue. All writes to mirrors go through this one queue, so they run one at a time.</summary>
internal static class GitJob
{
    public const string Queue = "git";
    public const string Clone = "clone";
    public const string Fetch = "fetch";
    public const string Delete = "delete";

    /// <summary>Joins the caller's transaction. A failed fetch is not retried: the next period fetches anyway.</summary>
    public static Task EnqueueAsync(IJobQueue jobs, string kind, Repository repository, CancellationToken cancellationToken) =>
        EnqueueAsync(jobs, kind, repository.Id, repository.ProjectId, cancellationToken);

    public static Task EnqueueAsync(IJobQueue jobs, string kind, Guid repositoryId, Guid projectId, CancellationToken cancellationToken) =>
        jobs.EnqueueAsync(
            Queue,
            JsonSerializer.Serialize(new Payload(kind, repositoryId, projectId), JsonSerializerOptions.Web),
            maxAttempts: kind == Fetch ? 1 : 3,
            cancellationToken: cancellationToken);

    public sealed record Payload(string Kind, Guid RepositoryId, Guid ProjectId);
}

/// <summary>
/// Runs clone, fetch and delete jobs. Git failures (unreachable remote, bad credential) are recorded on the
/// repository for people to see, and the job completes; only unexpected errors (database down) make it retry.
/// </summary>
internal sealed partial class GitJobHandler(
    TikaiDbContext db,
    IGitService git,
    MirrorPaths paths,
    RepositoryCredentials credentials,
    IOptions<RepositorySyncOptions> options,
    TimeProvider timeProvider,
    ILogger<GitJobHandler> logger) : IJobHandler
{
    public string Queue => GitJob.Queue;

    public Task HandleAsync(JobLease job, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<GitJob.Payload>(job.Payload, JsonSerializerOptions.Web)!;
        return payload.Kind switch
        {
            GitJob.Clone => CloneAsync(payload.RepositoryId, cancellationToken),
            GitJob.Fetch => FetchAsync(payload.RepositoryId, cancellationToken),
            GitJob.Delete => DeleteAsync(payload.ProjectId, payload.RepositoryId, cancellationToken),
            _ => throw new InvalidOperationException($"Unknown git job kind '{payload.Kind}'."),
        };
    }

    private async Task CloneAsync(Guid repositoryId, CancellationToken cancellationToken)
    {
        var repository = await db.Repositories.AsNoTracking().SingleOrDefaultAsync(r => r.Id == repositoryId, cancellationToken);
        if (repository is not { Status: RepositoryStatus.Cloning, RemoteUrl: not null })
        {
            return; // deleted, or already done
        }

        var path = paths.For(repository.ProjectId, repository.Id);
        await git.DeleteRepositoryAsync(path, cancellationToken); // left over from an interrupted attempt
        try
        {
            var result = await git.CreateMirrorAsync(
                path,
                GitRemoteUrl.Parse(repository.RemoteUrl),
                credentials.Unprotect(repository),
                repository.DefaultBranch.Length == 0 ? null : repository.DefaultBranch,
                cancellationToken);

            var now = timeProvider.GetUtcNow();
            var updated = await db.Repositories
                .Where(r => r.Id == repositoryId && r.Status == RepositoryStatus.Cloning)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, RepositoryStatus.Ready)
                    .SetProperty(r => r.DefaultBranch, result.DefaultBranch)
                    .SetProperty(r => r.LastError, (string?)null)
                    .SetProperty(r => r.LastFetchedAt, now)
                    .SetProperty(r => r.NextFetchAt, now + options.Value.FetchInterval)
                    .SetProperty(r => r.UpdatedAt, now), cancellationToken);
            if (updated == 0)
            {
                await git.DeleteRepositoryAsync(path, cancellationToken); // deleted while cloning
            }
        }
        catch (Exception ex) when (ex is GitException or CryptographicException)
        {
            LogCloneFailed(repositoryId, ex.Message);
            var now = timeProvider.GetUtcNow();
            await db.Repositories
                .Where(r => r.Id == repositoryId && r.Status == RepositoryStatus.Cloning)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, RepositoryStatus.Failed)
                    .SetProperty(r => r.LastError, ErrorMessage(ex))
                    .SetProperty(r => r.UpdatedAt, now), cancellationToken);
        }
    }

    private async Task FetchAsync(Guid repositoryId, CancellationToken cancellationToken)
    {
        var repository = await db.Repositories.AsNoTracking().SingleOrDefaultAsync(r => r.Id == repositoryId, cancellationToken);
        if (repository is not { Status: RepositoryStatus.Ready, RemoteUrl: not null })
        {
            return;
        }

        string? error;
        try
        {
            var result = await git.FetchAsync(
                paths.For(repository.ProjectId, repository.Id),
                GitRemoteUrl.Parse(repository.RemoteUrl),
                credentials.Unprotect(repository),
                repository.DefaultBranch,
                cancellationToken);
            error = result.DefaultBranch == DefaultBranchSync.Diverged
                ? $"The branch \"{repository.DefaultBranch}\" on the hub and on the remote have both changed; the hub's copy was left as it is."
                : null;
        }
        catch (Exception ex) when (ex is GitException or CryptographicException)
        {
            LogFetchFailed(repositoryId, ex.Message);
            error = ErrorMessage(ex);
        }

        var now = timeProvider.GetUtcNow();
        await db.Repositories
            .Where(r => r.Id == repositoryId)
            .ExecuteUpdateAsync(s =>
            {
                s.SetProperty(r => r.LastError, error).SetProperty(r => r.UpdatedAt, now);
                if (error is null)
                {
                    s.SetProperty(r => r.LastFetchedAt, now);
                }
            }, cancellationToken);
    }

    private async Task DeleteAsync(Guid projectId, Guid repositoryId, CancellationToken cancellationToken)
    {
        var path = paths.For(projectId, repositoryId);
        await git.DeleteRepositoryAsync(path, cancellationToken);
        var projectDirectory = Path.GetDirectoryName(path)!;
        if (Directory.Exists(projectDirectory) && !Directory.EnumerateFileSystemEntries(projectDirectory).Any())
        {
            Directory.Delete(projectDirectory);
        }
    }

    private static string ErrorMessage(Exception ex) => ex is CryptographicException
        ? "The stored credential cannot be decrypted (the hub's keys changed). Enter the credential again."
        : ex.Message;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Clone of repository {RepositoryId} failed: {Error}")]
    private partial void LogCloneFailed(Guid repositoryId, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fetch of repository {RepositoryId} failed: {Error}")]
    private partial void LogFetchFailed(Guid repositoryId, string error);
}
