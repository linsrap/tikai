using Microsoft.EntityFrameworkCore;
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
    RepositoryCredentials credentials,
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
