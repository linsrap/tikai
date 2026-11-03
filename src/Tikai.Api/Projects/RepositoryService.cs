using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Tikai.Core.Git;
using Tikai.Core.Jobs;
using Tikai.Core.Projects;
using Tikai.Data;

namespace Tikai.Api.Projects;

/// <summary>Creates, changes and removes repositories. Input is validated by the endpoints.</summary>
internal sealed class RepositoryService(
    TikaiDbContext db,
    IJobQueue jobs,
    IGitService git,
    MirrorPaths paths,
    MirrorLocks locks,
    RepositoryCredentials credentials,
    IOptions<RepositorySyncOptions> syncOptions,
    TimeProvider timeProvider)
{
    /// <summary>A new bare repository on the hub, ready at once. Throws <see cref="DbUpdateException"/> for a duplicate name.</summary>
    public async Task<Repository> CreateLocalAsync(Project project, string name, string defaultBranch, CancellationToken cancellationToken)
    {
        var repository = New(project, name, null, defaultBranch, RepositoryStatus.Ready);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Repositories.Add(repository);
        await db.SaveChangesAsync(cancellationToken);

        var path = paths.For(project.Id, repository.Id);
        try
        {
            await git.InitBareAsync(path, defaultBranch, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await git.DeleteRepositoryAsync(path, CancellationToken.None);
            throw;
        }
        return repository;
    }

    /// <summary>A mirror of a remote; the clone runs as a job. Throws <see cref="DbUpdateException"/> for a duplicate name.</summary>
    public async Task<Repository> CreateMirrorAsync(
        Project project, string name, GitRemoteUrl remote, string? defaultBranch, CredentialInput? credential, CancellationToken cancellationToken)
    {
        // An empty default branch means "detect it from the remote" until the clone has run.
        var repository = New(project, name, remote.Value, defaultBranch ?? "", RepositoryStatus.Cloning);
        (repository.CredentialKind, repository.EncryptedCredential) = credentials.Protect(credential);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Repositories.Add(repository);
        await db.SaveChangesAsync(cancellationToken);
        await GitJob.EnqueueAsync(jobs, GitJob.Clone, repository, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return repository;
    }

    public async Task SetCredentialAsync(Repository repository, CredentialInput? credential, CancellationToken cancellationToken)
    {
        (repository.CredentialKind, repository.EncryptedCredential) = credentials.Protect(credential);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Connects the repository to <paramref name="remote"/> (a local one gets its first remote, a remote one a new URL)
    /// under the mirror lock: the hub fetches, pushes its default branch when the remote is empty or behind, and only
    /// then stores the remote. Throws <see cref="GitException"/> with a reason for people when the histories do not fit
    /// or the remote cannot be reached; nothing changes then.
    /// </summary>
    /// <param name="credential">Null keeps the stored credential.</param>
    public async Task<bool> ConnectRemoteAsync(Repository repository, GitRemoteUrl remote, CredentialInput? credential, CancellationToken cancellationToken)
    {
        var (kind, encrypted) = credential is null ? (repository.CredentialKind, repository.EncryptedCredential) : credentials.Protect(credential);
        GitCredential? secret;
        try
        {
            secret = credentials.Unprotect(new Repository { Name = repository.Name, DefaultBranch = repository.DefaultBranch, CredentialKind = kind, EncryptedCredential = encrypted });
        }
        catch (CryptographicException)
        {
            throw new GitException("The stored credential cannot be decrypted (the hub's keys changed). Enter the credential again.");
        }

        ConnectResult result;
        using (await locks.AcquireAsync(repository.Id, cancellationToken))
        {
            result = await git.ConnectRemoteAsync(paths.For(repository.ProjectId, repository.Id), remote, secret, repository.DefaultBranch, cancellationToken);
        }
        var now = timeProvider.GetUtcNow();
        repository.RemoteUrl = remote.Value;
        (repository.CredentialKind, repository.EncryptedCredential) = (kind, encrypted);
        repository.LastError = null;
        repository.LastFetchedAt = now;
        repository.NextFetchAt = now + syncOptions.Value.FetchInterval;
        await db.SaveChangesAsync(cancellationToken);
        return result.Pushed;
    }

    /// <summary>Queues a fetch, or a new clone when the first one failed. Does nothing while a clone is queued.</summary>
    public async Task RequestFetchAsync(Repository repository, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        switch (repository.Status)
        {
            case RepositoryStatus.Ready:
                await GitJob.EnqueueAsync(jobs, GitJob.Fetch, repository, cancellationToken);
                break;
            case RepositoryStatus.Failed:
                repository.Status = RepositoryStatus.Cloning;
                repository.LastError = null;
                await db.SaveChangesAsync(cancellationToken);
                await GitJob.EnqueueAsync(jobs, GitJob.Clone, repository, cancellationToken);
                break;
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Removes the row now and the directory in a job, so it runs after any clone or fetch of this repository.</summary>
    public async Task DeleteAsync(Repository repository, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Repositories.Remove(repository);
        await db.SaveChangesAsync(cancellationToken);
        await GitJob.EnqueueAsync(jobs, GitJob.Delete, repository, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public static bool IsDuplicate(DbUpdateException ex) => ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private Repository New(Project project, string name, string? remoteUrl, string defaultBranch, RepositoryStatus status) => new()
    {
        Id = Guid.CreateVersion7(timeProvider.GetUtcNow()),
        ProjectId = project.Id,
        Name = name,
        RemoteUrl = remoteUrl,
        DefaultBranch = defaultBranch,
        Status = status,
    };
}
