using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tikai.Core.Jobs;
using Tikai.Data;

namespace Tikai.Api.Projects;

/// <summary>
/// Queues a fetch for every remote repository whose next fetch is due. Claiming (moving next_fetch_at on)
/// and enqueueing happen in one transaction with the database clock, so a repository is never queued twice per period.
/// </summary>
internal sealed partial class RepositoryFetchScheduler(
    IServiceScopeFactory scopes,
    IOptions<RepositorySyncOptions> options,
    TimeProvider timeProvider,
    ILogger<RepositoryFetchScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScheduleDueAsync(stoppingToken);
                await Task.Delay(options.Value.SchedulerInterval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Database unavailable or similar: try again next round.
                LogSchedulingFailed(ex);
                await Task.Delay(options.Value.SchedulerInterval, timeProvider, stoppingToken);
            }
        }
    }

    internal async Task<int> ScheduleDueAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TikaiDbContext>();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobQueue>();
        var seconds = options.Value.FetchInterval.TotalSeconds;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var due = await db.Database.SqlQuery<DueRepository>(
            $"""
            UPDATE repositories SET next_fetch_at = now() + make_interval(secs => {seconds})
            WHERE remote_url IS NOT NULL AND status = 'ready' AND (next_fetch_at IS NULL OR next_fetch_at <= now())
            RETURNING id, project_id
            """).ToListAsync(cancellationToken);
        foreach (var repository in due)
        {
            await GitJob.EnqueueAsync(jobs, GitJob.Fetch, repository.Id, repository.ProjectId, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return due.Count;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduling repository fetches failed")]
    private partial void LogSchedulingFailed(Exception exception);

    private sealed record DueRepository(Guid Id, Guid ProjectId);
}
