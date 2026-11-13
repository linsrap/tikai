using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tikai.Api.Projects;
using Tikai.Core.Events;
using Tikai.Core.Git;
using Tikai.Core.Jobs;
using Tikai.Core.Previews;
using Tikai.Core.Reviews;
using Tikai.Data;
using Tikai.Orchestrator.Previews;
using PreviewLogLine = Tikai.Core.Previews.PreviewLogLine;

namespace Tikai.Api.Previews;

/// <summary>The previews' database side for the jobs and the reconciler. State changes are compare-and-set.</summary>
internal sealed class PreviewStore(
    TikaiDbContext db,
    IJobQueue jobs,
    IEventPublisher events,
    IGitService git,
    MirrorPaths mirrors,
    IOptions<PreviewOptions> options,
    TimeProvider timeProvider) : IPreviewStore
{
    public async Task<PreviewStartInfo?> GetStartInfoAsync(Guid previewId, CancellationToken cancellationToken)
    {
        var target = await (
            from p in db.Previews
            join r in db.Repositories on p.RepositoryId equals r.Id
            where p.Id == previewId && p.State == PreviewState.Preparing
            select new { p.ChangeSetId, p.RoundNo, p.Branch, p.HeadSha, p.Definition, r.ProjectId, RepositoryId = r.Id }).SingleOrDefaultAsync(cancellationToken);
        if (target is null)
        {
            return null;
        }
        var reference = target.Branch is { } branch ? PreviewRules.BranchRef(branch) : ReviewRules.RoundRef(target.ChangeSetId!.Value, target.RoundNo!.Value);
        return new PreviewStartInfo(previewId, mirrors.For(target.ProjectId, target.RepositoryId), reference, target.HeadSha, target.Definition);
    }

    public async Task AppendLogAsync(Guid previewId, IReadOnlyList<PreviewLogEntry> lines, CancellationToken cancellationToken)
    {
        if (lines.Count == 0)
        {
            return;
        }
        var now = timeProvider.GetUtcNow();
        var max = options.Value.MaxLogLines;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // The counter row lock orders concurrent writers (the start job and the app's log follower).
        var last = await db.Database.SqlQuery<long>($"SELECT last_log_seq AS \"Value\" FROM previews WHERE id = {previewId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        if (last.Count == 0)
        {
            return;
        }
        var seq = last[0];
        var kept = new List<PreviewLogEntry>();
        foreach (var line in lines)
        {
            if (seq + kept.Count < max || line.Source == "hub")
            {
                kept.Add(line);
            }
            else if (seq + kept.Count == max)
            {
                kept.Add(new PreviewLogEntry("hub", $"The log is cut at {max} lines; only the hub's notes follow."));
            }
        }
        if (kept.Count == 0)
        {
            return;
        }
        foreach (var line in kept)
        {
            db.PreviewLogLines.Add(new PreviewLogLine
            {
                Id = Guid.CreateVersion7(now),
                PreviewId = previewId,
                Seq = ++seq,
                Source = line.Source.Length > PreviewRules.MaxSourceLength ? line.Source[..PreviewRules.MaxSourceLength] : line.Source,
                Text = line.Text,
            });
        }
        await db.Previews.Where(p => p.Id == previewId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.LastLogSeq, seq).SetProperty(p => p.UpdatedAt, now), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await events.PublishAsync(Preview.Topic, previewId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public Task<bool> IsActiveAsync(Guid previewId, CancellationToken cancellationToken) =>
        db.Previews.AnyAsync(p => p.Id == previewId && PreviewRules.Active.Contains(p.State), cancellationToken);

    public async Task<bool> MarkStartingAsync(Guid previewId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return await ChangeAsync(previewId, db.Previews.Where(p => p.Id == previewId && p.State == PreviewState.Preparing)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.State, PreviewState.Starting).SetProperty(p => p.UpdatedAt, now), cancellationToken), cancellationToken);
    }

    public async Task<bool> MarkRunningAsync(Guid previewId, IReadOnlyList<PreviewAppUpstream> apps, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var upstreams = PreviewUpstreams.ToJson(apps);
        return await ChangeAsync(previewId, db.Previews.Where(p => p.Id == previewId && p.State == PreviewState.Starting)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.State, PreviewState.Running)
                .SetProperty(p => p.Upstreams, upstreams)
                .SetProperty(p => p.RunningAt, now)
                .SetProperty(p => p.UpdatedAt, now), cancellationToken), cancellationToken);
    }

    public async Task<bool> FailAsync(Guid previewId, string reason, PreviewState? state, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var error = reason.Length > PreviewRules.MaxErrorLength ? reason[..PreviewRules.MaxErrorLength] : reason;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var changed = await db.Previews
            .Where(p => p.Id == previewId && PreviewRules.Active.Contains(p.State) && (state == null || p.State == state))
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.State, PreviewState.Failed)
                .SetProperty(p => p.Error, error)
                .SetProperty(p => p.Upstreams, (string?)null)
                .SetProperty(p => p.StoppedAt, now)
                .SetProperty(p => p.UpdatedAt, now), cancellationToken);
        if (changed == 0)
        {
            return false;
        }
        await PreviewStops.EnqueueTeardownAsync(jobs, previewId, cancellationToken);
        await events.PublishAsync(Preview.Topic, previewId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> StopAsync(Guid previewId, string reason, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await PreviewStops.StopAsync(db, jobs, events, previewId, reason, timeProvider.GetUtcNow(), cancellationToken))
        {
            return false;
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public Task<bool> NeedsCleanupAsync(Guid previewId, CancellationToken cancellationToken) =>
        db.Previews.AnyAsync(p => p.Id == previewId && !PreviewRules.Active.Contains(p.State) && p.CleanedAt == null, cancellationToken);

    public async Task MarkCleanedAsync(Guid previewId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await db.Previews.Where(p => p.Id == previewId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.CleanedAt, now).SetProperty(p => p.UpdatedAt, now), cancellationToken);
    }

    public async Task<IReadOnlyList<ActivePreview>> ListActiveAsync(CancellationToken cancellationToken)
    {
        var active = PreviewRules.Active.Select(s => JsonNamingPolicy.SnakeCaseLower.ConvertName(s.ToString())).ToArray();
        var rows = await db.Database.SqlQuery<ActiveRow>($"""
            SELECT p.id AS id, p.state AS state, COALESCE(c.status IN ('open', 'delivering'), false) AS change_set_open,
                   p.branch AS branch, p.head_sha AS head_sha, r.project_id AS project_id, p.repository_id AS repository_id,
                   (pr.archived_at IS NOT NULL OR pr.deleting_at IS NOT NULL) AS project_closed, p.upstreams::text AS upstreams,
                   EXISTS (SELECT 1 FROM jobs j WHERE j.queue = {PreviewJob.StartQueue} AND j.dead_at IS NULL AND j.payload->>'preview_id' = p.id::text) AS has_live_start_job,
                   EXTRACT(EPOCH FROM now() - p.created_at)::float8 AS age_seconds,
                   EXTRACT(EPOCH FROM now() - COALESCE(p.last_request_at, p.running_at))::float8 AS idle_seconds,
                   p.idle_minutes AS idle_minutes
            FROM previews p
            JOIN repositories r ON r.id = p.repository_id
            JOIN projects pr ON pr.id = r.project_id
            LEFT JOIN change_sets c ON c.id = p.change_set_id
            WHERE p.state = ANY({active})
            """).ToListAsync(cancellationToken);
        var previews = new List<ActivePreview>();
        foreach (var r in rows)
        {
            previews.Add(new ActivePreview(
                r.Id, Enum.Parse<PreviewState>(r.State.Replace("_", "", StringComparison.Ordinal), ignoreCase: true), await EndReasonAsync(r, cancellationToken),
                PreviewUpstreams.FromJson(r.Upstreams), r.HasLiveStartJob,
                TimeSpan.FromSeconds(r.AgeSeconds), r.IdleSeconds is { } idle ? TimeSpan.FromSeconds(idle) : null, r.IdleMinutes));
        }
        return previews;
    }

    /// <summary>A round's preview ends with its change set; a branch preview with its project, or when the branch moved.</summary>
    private async Task<string?> EndReasonAsync(ActiveRow row, CancellationToken cancellationToken)
    {
        if (row.Branch is not { } branch)
        {
            return row.ChangeSetOpen ? null : PreviewReconciliation.ChangeSetEnded;
        }
        if (row.ProjectClosed)
        {
            return PreviewReconciliation.ProjectClosed;
        }
        var head = await git.ResolveCommitAsync(mirrors.For(row.ProjectId, row.RepositoryId), PreviewRules.BranchRef(branch), cancellationToken);
        return head == row.HeadSha ? null : PreviewReconciliation.BranchMoved(branch, head);
    }

    public async Task<IReadOnlyList<Guid>> ListUncleanedAsync(CancellationToken cancellationToken)
    {
        var active = PreviewRules.Active.Select(s => JsonNamingPolicy.SnakeCaseLower.ConvertName(s.ToString())).ToArray();
        return await db.Database.SqlQuery<Guid>($"""
            SELECT p.id AS "Value" FROM previews p
            WHERE NOT (p.state = ANY({active})) AND p.cleaned_at IS NULL
              AND NOT EXISTS (SELECT 1 FROM jobs j WHERE j.queue = {PreviewJob.TeardownQueue} AND j.dead_at IS NULL AND j.payload->>'preview_id' = p.id::text)
            ORDER BY p.stopped_at
            LIMIT 20
            """).ToListAsync(cancellationToken);
    }

    public async Task RequestTeardownAsync(Guid previewId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({"preview-teardown:" + previewId.ToString("D")}))", cancellationToken);
        var live = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (
                SELECT 1 FROM jobs
                WHERE queue IN ({PreviewJob.StartQueue}, {PreviewJob.TeardownQueue}) AND dead_at IS NULL AND payload->>'preview_id' = {previewId.ToString("D")}) AS "Value"
            """).SingleAsync(cancellationToken);
        if (live || await IsActiveAsync(previewId, cancellationToken))
        {
            return;
        }
        await PreviewStops.EnqueueTeardownAsync(jobs, previewId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<bool> ChangeAsync(Guid previewId, Task<int> update, CancellationToken cancellationToken)
    {
        if (await update == 0)
        {
            return false;
        }
        await events.PublishAsync(Preview.Topic, previewId, cancellationToken);
        return true;
    }

    private sealed record ActiveRow(
        Guid Id, string State, bool ChangeSetOpen, string? Branch, string HeadSha, Guid ProjectId, Guid RepositoryId, bool ProjectClosed,
        string? Upstreams, bool HasLiveStartJob, double AgeSeconds, double? IdleSeconds, int IdleMinutes);
}
