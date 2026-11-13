using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tikai.Api.Previews;
using Tikai.Core.Events;
using Tikai.Core.Jobs;
using Tikai.Core.Previews;
using Tikai.Core.Reviews;
using Tikai.Core.Runs;
using Tikai.Data;
using Tikai.Orchestrator;
using Tikai.Orchestrator.Previews;

namespace Tikai.Api.Projects;

/// <summary>The job that deletes a project once nothing of it runs any more (see <see cref="ProjectDeletionHandler"/>).</summary>
internal static class ProjectJob
{
    public const string Queue = "projects";

    /// <param name="Polls">How often the job has looked and found something still running.</param>
    public sealed record Payload(Guid ProjectId, int Polls = 0);

    /// <summary>Joins the caller's transaction, if any.</summary>
    public static Task EnqueueAsync(IJobQueue jobs, Payload payload, DateTimeOffset? runAt, CancellationToken cancellationToken) =>
        jobs.EnqueueAsync(Queue, JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), runAt, maxAttempts: 5, cancellationToken: cancellationToken);
}

/// <summary>
/// Archiving and deleting a project. Both first stop what runs for it: active runs are cancelled (their change sets
/// close, their directories are cleaned up by the usual job) and previews are stopped. The flag is set first, so
/// nothing new starts meanwhile (<see cref="ProjectEndpoints.Closed"/>, and the run start checks it under a row lock).
/// </summary>
internal sealed partial class ProjectLifecycle(
    TikaiDbContext db,
    RunControl control,
    IJobQueue jobs,
    IEventPublisher events,
    TimeProvider timeProvider,
    ILogger<ProjectLifecycle> logger)
{
    public const string ArchivedReason = "The project was archived.";
    public const string DeletingReason = "The project is being deleted.";

    /// <summary>An approved change of the project is being merged or pushed; that cannot be cancelled half-way.</summary>
    public Task<bool> IsDeliveringAsync(Guid projectId, CancellationToken cancellationToken) =>
        db.ChangeSets.AnyAsync(
            c => c.Status == ChangeSetStatus.Delivering && db.Tickets.Any(t => t.Id == c.TicketId && t.ProjectId == projectId), cancellationToken);

    /// <summary>Archives the project and stops its runs and previews. Doing it again stops what is left.</summary>
    public async Task ArchiveAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await db.Projects.Where(p => p.Id == projectId && p.ArchivedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.ArchivedAt, now).SetProperty(p => p.UpdatedAt, now), cancellationToken);
            await StopPreviewsAsync(projectId, ArchivedReason, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        await CancelRunsAsync(projectId, cancellationToken);
    }

    public async Task RestoreAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await db.Projects.Where(p => p.Id == projectId && p.DeletingAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.ArchivedAt, (DateTimeOffset?)null).SetProperty(p => p.UpdatedAt, now), cancellationToken);
    }

    /// <summary>
    /// Marks the project as being deleted, stops its runs and previews and queues the deletion job. Asking again
    /// queues the job again when the earlier one gave up.
    /// </summary>
    public async Task RequestDeleteAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await db.Projects.Where(p => p.Id == projectId && p.DeletingAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.DeletingAt, now).SetProperty(p => p.UpdatedAt, now), cancellationToken);
            await StopPreviewsAsync(projectId, DeletingReason, now, cancellationToken);
            var queued = await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS (
                    SELECT 1 FROM jobs
                    WHERE queue = {ProjectJob.Queue} AND dead_at IS NULL AND payload->>'projectId' = {projectId.ToString("D")}) AS "Value"
                """).SingleAsync(cancellationToken);
            if (!queued)
            {
                await ProjectJob.EnqueueAsync(jobs, new ProjectJob.Payload(projectId), null, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        try
        {
            await CancelRunsAsync(projectId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The job cancels them again each time it looks.
            LogCancelFailed(projectId, ex);
        }
    }

    /// <summary>Stops the project's runs and previews (again); the deletion job calls it each time it looks.</summary>
    public async Task StopAsync(Guid projectId, string reason, CancellationToken cancellationToken)
    {
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await StopPreviewsAsync(projectId, reason, timeProvider.GetUtcNow(), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        await CancelRunsAsync(projectId, cancellationToken);
    }

    /// <summary>A container is stopped and the monitor ends its run a moment later; a run without one ends at once.</summary>
    private async Task CancelRunsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var active = await db.AgentRuns
            .Where(r => !AgentRunStates.Ended.Contains(r.State) && db.Tickets.Any(t => t.Id == r.TicketId && t.ProjectId == projectId))
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);
        foreach (var runId in active)
        {
            // Approved meanwhile: the delivery ends the run.
            if (!await db.ChangeSets.AnyAsync(c => c.RunId == runId && c.Status == ChangeSetStatus.Delivering, cancellationToken))
            {
                await control.CancelAsync(runId, cancellationToken);
            }
        }
    }

    /// <summary>Inside the caller's transaction.</summary>
    private async Task StopPreviewsAsync(Guid projectId, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var active = await ProjectRows.Previews(db, projectId).Where(p => PreviewRules.Active.Contains(p.State)).Select(p => p.Id).ToListAsync(cancellationToken);
        foreach (var previewId in active)
        {
            await PreviewStops.StopAsync(db, jobs, events, previewId, reason, now, cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Project {ProjectId}: cancelling its runs failed; the deletion job tries again")]
    private partial void LogCancelFailed(Guid projectId, Exception exception);
}

/// <summary>What belongs to a project: runs through its tickets, previews through its repositories.</summary>
internal static class ProjectRows
{
    public static IQueryable<AgentRun> Runs(TikaiDbContext db, Guid projectId) =>
        db.AgentRuns.Where(r => db.Tickets.Any(t => t.Id == r.TicketId && t.ProjectId == projectId));

    public static IQueryable<Preview> Previews(TikaiDbContext db, Guid projectId) =>
        db.Previews.Where(p => db.Repositories.Any(r => r.Id == p.RepositoryId && r.ProjectId == projectId));
}

/// <summary>
/// Deletes a project with everything in it (queue <see cref="ProjectJob.Queue"/>). Rows go only when nothing is left
/// outside the database that needs them: every run has ended and its directory is removed (the cleanup job needs the
/// run row), every preview is stopped and torn down. Until then the job stops what is left and looks again later.
/// Then all rows go in one transaction, and each repository's mirror through the git queue's delete job, which runs
/// after any clone or fetch still queued. Only the hub's copies are removed; a remote is never contacted.
/// </summary>
internal sealed partial class ProjectDeletionHandler(
    TikaiDbContext db,
    ProjectLifecycle lifecycle,
    IJobQueue jobs,
    IOptions<DockerOptions> docker,
    TimeProvider timeProvider,
    ILogger<ProjectDeletionHandler> logger) : IJobHandler
{
    /// <summary>Quick looks first (a cancelled agent needs a few seconds), then slow ones.</summary>
    private const int QuickPolls = 60;

    public string Queue => ProjectJob.Queue;

    public async Task HandleAsync(JobLease job, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<ProjectJob.Payload>(job.Payload, JsonSerializerOptions.Web)!;
        var projectId = payload.ProjectId;
        if (!await db.Projects.AnyAsync(p => p.Id == projectId && p.DeletingAt != null, cancellationToken))
        {
            return; // gone already
        }
        await lifecycle.StopAsync(projectId, ProjectLifecycle.DeletingReason, cancellationToken);
        var waiting = await WaitingForAsync(projectId, cancellationToken);
        if (waiting is null && await DeleteAsync(projectId, cancellationToken))
        {
            LogDeleted(projectId);
            return;
        }
        if (payload.Polls % QuickPolls == 0)
        {
            LogWaiting(projectId, waiting ?? "something started meanwhile");
        }
        var delay = payload.Polls < QuickPolls ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(30);
        await ProjectJob.EnqueueAsync(jobs, payload with { Polls = payload.Polls + 1 }, timeProvider.GetUtcNow() + delay, cancellationToken);
    }

    /// <summary>What still keeps the project's rows, in words for the log; null when they can go.</summary>
    private async Task<string?> WaitingForAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var runs = ProjectRows.Runs(db, projectId);
        var previews = ProjectRows.Previews(db, projectId);
        if (await runs.CountAsync(r => !AgentRunStates.Ended.Contains(r.State), cancellationToken) is > 0 and var active)
        {
            return $"{active} of its runs have not ended";
        }
        if (await previews.CountAsync(p => PreviewRules.Active.Contains(p.State), cancellationToken) is > 0 and var running)
        {
            return $"{running} of its previews have not stopped";
        }
        if (!docker.Value.Enabled)
        {
            return null; // no Docker, so no volume this hub could clean
        }
        if (await runs.CountAsync(r => r.CleanedAt == null, cancellationToken) is > 0 and var uncleaned)
        {
            return $"the directories of {uncleaned} runs are not removed yet";
        }
        if (await previews.CountAsync(p => p.CleanedAt == null, cancellationToken) is > 0 and var standing)
        {
            return $"{standing} previews are not torn down yet";
        }
        return null;
    }

    private async Task<bool> DeleteAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // A run start holds a share lock on this row while it checks the flag; nothing starts after this.
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM projects WHERE id = {projectId} FOR UPDATE", cancellationToken);
        if (await WaitingForAsync(projectId, cancellationToken) is not null)
        {
            return false;
        }
        var repositories = await db.Repositories.Where(r => r.ProjectId == projectId).Select(r => r.Id).ToListAsync(cancellationToken);
        var id = projectId.ToString("D");

        // Jobs that gave up stay in the table for people to look at; these would point at nothing.
        await db.Database.ExecuteSqlAsync($"""
            DELETE FROM jobs j WHERE j.dead_at IS NOT NULL AND (
                (j.queue = {RunJob.Queue} AND j.payload->>'run_id' IN (
                    SELECT r.id::text FROM agent_runs r JOIN tickets t ON t.id = r.ticket_id WHERE t.project_id = {projectId}))
                OR (j.queue IN ({PreviewJob.StartQueue}, {PreviewJob.TeardownQueue}) AND j.payload->>'preview_id' IN (
                    SELECT p.id::text FROM previews p JOIN repositories r ON r.id = p.repository_id WHERE r.project_id = {projectId}))
                OR (j.queue IN ({GitJob.Queue}, {ProjectJob.Queue}) AND j.payload->>'projectId' = {id}))
            """, cancellationToken);
        // Children before parents: the foreign keys restrict, except where a row is nothing without its parent
        // (log lines, entries, comments, viewed marks, run events, criteria, the project's providers).
        await db.Database.ExecuteSqlAsync($"""
            DELETE FROM previews WHERE repository_id IN (SELECT id FROM repositories WHERE project_id = {projectId})
            """, cancellationToken);
        await db.Database.ExecuteSqlAsync($"""
            DELETE FROM review_threads WHERE change_set_id IN (
                SELECT c.id FROM change_sets c JOIN tickets t ON t.id = c.ticket_id WHERE t.project_id = {projectId})
            """, cancellationToken);
        await db.Database.ExecuteSqlAsync($"""
            DELETE FROM review_rounds WHERE change_set_id IN (
                SELECT c.id FROM change_sets c JOIN tickets t ON t.id = c.ticket_id WHERE t.project_id = {projectId})
            """, cancellationToken);
        await db.Database.ExecuteSqlAsync($"DELETE FROM change_sets WHERE ticket_id IN (SELECT id FROM tickets WHERE project_id = {projectId})", cancellationToken);
        await db.Database.ExecuteSqlAsync($"DELETE FROM attention_items WHERE ticket_id IN (SELECT id FROM tickets WHERE project_id = {projectId})", cancellationToken);
        await db.Database.ExecuteSqlAsync($"DELETE FROM agent_runs WHERE ticket_id IN (SELECT id FROM tickets WHERE project_id = {projectId})", cancellationToken);
        await db.Database.ExecuteSqlAsync($"DELETE FROM tickets WHERE project_id = {projectId}", cancellationToken);
        await db.Database.ExecuteSqlAsync($"DELETE FROM repositories WHERE project_id = {projectId}", cancellationToken);
        await db.Database.ExecuteSqlAsync($"DELETE FROM projects WHERE id = {projectId}", cancellationToken);
        foreach (var repositoryId in repositories)
        {
            await GitJob.EnqueueAsync(jobs, GitJob.Delete, repositoryId, projectId, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId} deleted with its tickets, runs, change sets, previews and repositories")]
    private partial void LogDeleted(Guid projectId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId} is not deleted yet: {Reason}")]
    private partial void LogWaiting(Guid projectId, string reason);
}
