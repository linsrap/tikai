using Microsoft.EntityFrameworkCore;
using Tikai.Core.Events;
using Tikai.Core.Jobs;
using Tikai.Core.Previews;
using Tikai.Data;
using Tikai.Orchestrator.Previews;

namespace Tikai.Api.Previews;

/// <summary>Stopping previews: the state changes at once (the proxy refuses it from then on), a teardown job removes the rest.</summary>
internal static class PreviewStops
{
    public const string ChangeSetEnded = PreviewReconciliation.ChangeSetEnded;

    public const string Replaced = "Replaced by a new preview.";

    /// <summary>An active preview → stopped, with its teardown queued. Joins the caller's transaction.</summary>
    public static async Task<bool> StopAsync(
        TikaiDbContext db, IJobQueue jobs, IEventPublisher events, Guid previewId, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var changed = await db.Previews
            .Where(p => p.Id == previewId && PreviewRules.Active.Contains(p.State))
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.State, PreviewState.Stopped)
                .SetProperty(p => p.StopReason, reason)
                .SetProperty(p => p.Upstreams, (string?)null)
                .SetProperty(p => p.StoppedAt, now)
                .SetProperty(p => p.UpdatedAt, now), cancellationToken);
        if (changed == 0)
        {
            return false;
        }
        await EnqueueTeardownAsync(jobs, previewId, cancellationToken);
        await events.PublishAsync(Preview.Topic, previewId, cancellationToken);
        return true;
    }

    /// <summary>Stops the change set's active preview, e.g. when it is merged, pushed or closed. Joins the caller's transaction.</summary>
    public static async Task ForChangeSetAsync(
        TikaiDbContext db, IJobQueue jobs, IEventPublisher events, Guid changeSetId, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var active = await db.Previews
            .Where(p => p.ChangeSetId == changeSetId && PreviewRules.Active.Contains(p.State))
            .Select(p => p.Id).ToListAsync(cancellationToken);
        foreach (var id in active)
        {
            await StopAsync(db, jobs, events, id, reason, now, cancellationToken);
        }
    }

    /// <summary>Stops the repository's active branch preview, e.g. before a new one starts. Joins the caller's transaction.</summary>
    public static async Task ForBranchAsync(
        TikaiDbContext db, IJobQueue jobs, IEventPublisher events, Guid repositoryId, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var active = await db.Previews
            .Where(p => p.RepositoryId == repositoryId && p.Branch != null && PreviewRules.Active.Contains(p.State))
            .Select(p => p.Id).ToListAsync(cancellationToken);
        foreach (var id in active)
        {
            await StopAsync(db, jobs, events, id, reason, now, cancellationToken);
        }
    }

    /// <summary>Joins the caller's transaction.</summary>
    public static Task EnqueueTeardownAsync(IJobQueue jobs, Guid previewId, CancellationToken cancellationToken) =>
        jobs.EnqueueAsync(PreviewJob.TeardownQueue, new PreviewJob(previewId).ToJson(), maxAttempts: 3, cancellationToken: cancellationToken);
}
