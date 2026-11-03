using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Tikai.Api.Attention;
using Tikai.Api.Llm;
using Tikai.Api.Projects;
using Tikai.Api.Tickets;
using Tikai.Core.Agents;
using Tikai.Core.Attention;
using Tikai.Core.Events;
using Tikai.Core.Git;
using Tikai.Core.Jobs;
using Tikai.Core.Reviews;
using Tikai.Core.Runs;
using Tikai.Core.Tickets;
using Tikai.Data;
using Tikai.Data.Runs;
using Tikai.Orchestrator;
using Tikai.Review;
using Tikai.Review.Analysis;

namespace Tikai.Api.Reviews;

/// <summary>
/// The review screen's API. Diffs and file contents are computed in the mirror on every request, between a round's base
/// and head; blob ids come from that diff, never from the client.
/// </summary>
internal static class ChangeSetEndpoints
{
    public const int MaxFiles = 3_000;

    public static void MapChangeSetEndpoints(this RouteGroupBuilder api)
    {
        var changeSets = api.MapGroup("/change-sets").WithTags("Reviews").RequireAuthorization();
        changeSets.MapGet("/{id:guid}", GetAsync);
        changeSets.MapGet("/{id:guid}/files", FilesAsync);
        changeSets.MapGet("/{id:guid}/file", FileAsync);
        changeSets.MapPut("/{id:guid}/viewed", ViewedAsync);
        changeSets.MapPost("/{id:guid}/verdict", VerdictAsync);
        changeSets.MapPost("/{id:guid}/rounds/{roundNo:int}/analysis", RetryAnalysisAsync);
    }

    private static async Task<Results<Ok<ChangeSetInfo>, ProblemHttpResult>> GetAsync(Guid id, TikaiDbContext db, CancellationToken cancellationToken) =>
        await InfoAsync(db, id, cancellationToken) is { } info ? TypedResults.Ok(info) : NotFound();

    /// <summary>The round's changed files (default: the latest round), with flags and the caller's viewed state.</summary>
    private static async Task<Results<Ok<ReviewFilesInfo>, ProblemHttpResult>> FilesAsync(
        Guid id, ClaimsPrincipal user, TikaiDbContext db, IGitService git, MirrorPaths mirrors, CancellationToken cancellationToken, int? round = null)
    {
        if (await RoundAsync(db, mirrors, id, round, cancellationToken) is not { } target)
        {
            return NotFound();
        }
        var diff = await git.DiffAsync(target.Mirror, target.Round.BaseSha, target.Round.HeadSha, MaxFiles, cancellationToken);
        var classified = await ClassifyAsync(git, target.Mirror, target.Round.HeadSha, diff, cancellationToken);
        var userId = LlmProviderService.UserId(user);
        var viewed = await db.FileViewStates.AsNoTracking()
            .Where(v => v.UserId == userId && v.ChangeSetId == id)
            .ToDictionaryAsync(v => v.FilePath, v => v.ViewedSha, StringComparer.Ordinal, cancellationToken);

        var files = diff.Files.Select((f, i) =>
        {
            var flags = classified[i].Flags;
            return new ReviewFileInfo(
                f.Path,
                f.Kind == GitChangeKind.Renamed ? f.OldPath : null,
                f.Kind,
                f.Additions,
                f.Deletions,
                Binary: f.Additions is null,
                flags.Lock,
                flags.Generated,
                flags.Large,
                viewed.TryGetValue(f.Path, out var sha) && sha == ReviewRules.ViewKey(f.NewBlob),
                Languages.For(f.Path));
        }).ToList();
        return TypedResults.Ok(new ReviewFilesInfo(target.Round.RoundNo, target.Round.BaseSha, target.Round.HeadSha, files, diff.Truncated));
    }

    /// <summary>The diff's files with their review flags (the head's .gitattributes counts), in the diff's order.</summary>
    internal static async Task<IReadOnlyList<AnalysisFile>> ClassifyAsync(
        IGitService git, string mirror, string headSha, GitDiff diff, CancellationToken cancellationToken)
    {
        var attributes = await git.ReadFileAsync(mirror, headSha, ".gitattributes", cancellationToken);
        var classifier = new FileClassifier(attributes is null ? null : FileText.Decode(attributes));
        return [.. diff.Files.Select(f => new AnalysisFile(
            f.Path,
            f.Kind == GitChangeKind.Renamed ? f.OldPath : null,
            f.Kind,
            f.Additions,
            f.Deletions,
            classifier.Classify(f.Path, null, f.Additions, f.Deletions)))];
    }

    /// <summary>Both sides of one changed file of the round, as text when it is text and not too large.</summary>
    private static async Task<Results<Ok<FileContentsInfo>, ProblemHttpResult>> FileAsync(
        Guid id, string path, TikaiDbContext db, IGitService git, MirrorPaths mirrors, CancellationToken cancellationToken, int? round = null)
    {
        if (await RoundAsync(db, mirrors, id, round, cancellationToken) is not { } target)
        {
            return NotFound();
        }
        var diff = await git.DiffAsync(target.Mirror, target.Round.BaseSha, target.Round.HeadSha, int.MaxValue, cancellationToken);
        if (diff.Files.FirstOrDefault(f => f.Path == path) is not { } file)
        {
            return ProjectEndpoints.NotFound("File");
        }
        return TypedResults.Ok(new FileContentsInfo(
            file.Path,
            file.Kind == GitChangeKind.Renamed ? file.OldPath : null,
            await SideAsync(git, target.Mirror, file.OldBlob, cancellationToken),
            await SideAsync(git, target.Mirror, file.NewBlob, cancellationToken),
            Languages.For(file.Path)));
    }

    /// <summary>
    /// Marks a file of the round as viewed (or not) for the caller. The hub stores the file's blob id in that round, so
    /// a later round that changes the file shows it unviewed again.
    /// </summary>
    private static async Task<Results<Ok<ViewedInfo>, ValidationProblem, ProblemHttpResult>> ViewedAsync(
        Guid id, ViewedRequest request, ClaimsPrincipal user, TikaiDbContext db, IGitService git, MirrorPaths mirrors,
        TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.Path) || request.Path.Length > ReviewRules.MaxFilePathLength)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["path"] = [$"Give a path of 1 to {ReviewRules.MaxFilePathLength} characters."] });
        }
        if (await RoundAsync(db, mirrors, id, request.Round, cancellationToken) is not { } target)
        {
            return NotFound();
        }
        var diff = await git.DiffAsync(target.Mirror, target.Round.BaseSha, target.Round.HeadSha, int.MaxValue, cancellationToken);
        if (diff.Files.FirstOrDefault(f => f.Path == request.Path) is not { } file)
        {
            return ProjectEndpoints.NotFound("File");
        }

        var userId = LlmProviderService.UserId(user);
        if (request.Viewed)
        {
            var now = timeProvider.GetUtcNow();
            var key = ReviewRules.ViewKey(file.NewBlob);
            // Two clicks at once must not fail on the unique index.
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO file_view_states (id, user_id, change_set_id, file_path, viewed_sha, created_at, updated_at)
                VALUES ({Guid.CreateVersion7(now)}, {userId}, {id}, {file.Path}, {key}, {now}, {now})
                ON CONFLICT (user_id, change_set_id, file_path) DO UPDATE SET viewed_sha = excluded.viewed_sha, updated_at = excluded.updated_at
                """, cancellationToken);
        }
        else
        {
            await db.FileViewStates.Where(v => v.UserId == userId && v.ChangeSetId == id && v.FilePath == file.Path).ExecuteDeleteAsync(cancellationToken);
        }
        return TypedResults.Ok(new ViewedInfo(file.Path, request.Viewed));
    }

    /// <summary>
    /// The reviewer's verdict on the latest round. Reject closes the run (cancelled), the change set and the review item;
    /// the ticket goes back to open. Approve (merge) and request changes (a fix round) come in later versions.
    /// </summary>
    private static async Task<Results<Ok<ChangeSetInfo>, ProblemHttpResult>> VerdictAsync(
        Guid id, VerdictRequest request, ClaimsPrincipal user, TikaiDbContext db, AttentionService attention, RunControl control,
        IEventPublisher events, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var changeSet = await db.ChangeSets.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (changeSet is null)
        {
            return NotFound();
        }
        if (request.Verdict != ReviewVerdict.Reject)
        {
            return Conflict("Not available yet", request.Verdict == ReviewVerdict.Approve
                ? "Approving merges the change; merging is not built yet. Fetch the run branch from the hub to merge it by hand."
                : "Requesting changes resumes the agent with your comments; that is not built yet.");
        }
        var run = await db.AgentRuns.AsNoTracking().SingleAsync(r => r.Id == changeSet.RunId, cancellationToken);
        if (changeSet.Status != ChangeSetStatus.Open || run.State != AgentRunState.InReview)
        {
            return Conflict("Not in review", "This change is not waiting for a review (it was closed, or its run ended).");
        }

        var userId = LlmProviderService.UserId(user);
        var now = timeProvider.GetUtcNow();
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            var latest = await db.ReviewRounds.Where(r => r.ChangeSetId == id).MaxAsync(r => r.RoundNo, cancellationToken);
            var decided = await db.ReviewRounds.Where(r => r.ChangeSetId == id && r.RoundNo == latest && r.Verdict == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Verdict, request.Verdict)
                    .SetProperty(r => r.VerdictById, userId)
                    .SetProperty(r => r.VerdictAt, now)
                    .SetProperty(r => r.UpdatedAt, now), cancellationToken);
            if (decided == 0)
            {
                return Conflict("Already decided", "Someone gave a verdict on this round first. Reload to see it.");
            }
            var reviewItems = await db.AttentionItems
                .Where(i => i.RunId == run.Id && i.Kind == AttentionKind.Review && i.Status == AttentionStatus.Open)
                .Select(i => i.Id).ToListAsync(cancellationToken);
            foreach (var item in reviewItems)
            {
                await attention.DismissAsync(item, userId, cancellationToken);
            }
            await db.AppendAsync(run.Id,
                [new ParsedEvent(RunEventKind.Log, new System.Text.Json.Nodes.JsonObject { ["source"] = "hub", ["text"] = $"Rejected in review (round {latest})" })],
                null, now, cancellationToken);
            await events.PublishAsync(RunEvent.Topic, run.Id, cancellationToken);
            await events.PublishAsync(ChangeSet.Topic, id, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        // The run ends cancelled (its container is stopped if it is still finishing); that closes the change set.
        await control.CancelAsync(run.Id, cancellationToken);
        return TypedResults.Ok((await InfoAsync(db, id, cancellationToken))!);
    }

    private static async Task<FileSide?> SideAsync(IGitService git, string mirror, string? blob, CancellationToken cancellationToken)
    {
        if (blob is null || await git.ReadBlobAsync(mirror, blob, FileClassifier.MaxShownBytes, cancellationToken) is not { } content)
        {
            return null;
        }
        if (content.Content is null)
        {
            return new FileSide(content.Size, null, Binary: false, TooLarge: true);
        }
        var text = FileText.Decode(content.Content);
        return new FileSide(content.Size, text, Binary: text is null, TooLarge: false);
    }

    private static async Task<ChangeSetInfo?> InfoAsync(TikaiDbContext db, Guid id, CancellationToken cancellationToken)
    {
        var row = await (
            from c in db.ChangeSets
            join r in db.AgentRuns on c.RunId equals r.Id
            join t in db.Tickets on c.TicketId equals t.Id
            join p in db.Projects on t.ProjectId equals p.Id
            join repo in db.Repositories on c.RepositoryId equals repo.Id
            where c.Id == id
            select new { ChangeSet = c, Run = r, TicketNumber = t.Number, t.Title, p.Slug, p.Key, RepositoryName = repo.Name, repo.DefaultBranch })
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }
        var criteria = await db.AcceptanceCriteria.AsNoTracking().Where(c => c.TicketId == row.ChangeSet.TicketId)
            .OrderBy(c => c.Position).ToListAsync(cancellationToken);
        var texts = criteria.ToDictionary(c => c.Id, c => c.Text);
        var rounds = await db.ReviewRounds.AsNoTracking().Where(r => r.ChangeSetId == id).OrderBy(r => r.RoundNo).ToListAsync(cancellationToken);
        return new ChangeSetInfo(
            row.ChangeSet.Id,
            row.ChangeSet.Status,
            row.Slug,
            row.TicketNumber,
            TicketKey.Format(row.Key, row.TicketNumber),
            row.Title,
            row.Run.Id,
            row.Run.State,
            row.Run.Model,
            row.Run.Branch ?? "",
            row.RepositoryName,
            row.DefaultBranch,
            [.. criteria.Select(c => new CriterionInfo(c.Id, c.Text))],
            [.. rounds.Select(r =>
            {
                var report = AttentionJson.Deserialize<ReviewPayload>(r.SelfReport);
                return new ReviewRoundInfo(
                    r.RoundNo,
                    r.BaseSha,
                    r.HeadSha,
                    report,
                    [.. report.CriteriaMapping.Select(m => new ReviewCoverage(m.CriterionId, texts.GetValueOrDefault(m.CriterionId), m.Status, m.Note))],
                    r.Uncommitted,
                    r.Verdict,
                    r.VerdictAt,
                    r.CreatedAt,
                    Analysis(r));
            })],
            row.ChangeSet.CreatedAt);
    }

    internal static AnalysisInfo? Analysis(ReviewRound round) => round.AnalysisStatus is { } status
        ? new AnalysisInfo(status, round.AnalysisError, round.AnalysisAt, round.Analysis is { } json ? AttentionJson.Deserialize<ReviewAnalysis>(json) : null)
        : null;

    /// <summary>
    /// Analyses the round again (after a failure, or with other models): its status goes back to pending and a job is
    /// queued. Refused while an analysis is pending, and for rounds from before analyses existed.
    /// </summary>
    private static async Task<Results<Ok<ChangeSetInfo>, ProblemHttpResult>> RetryAnalysisAsync(
        Guid id, int roundNo, TikaiDbContext db, IJobQueue jobs, IEventPublisher events, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var round = await db.ReviewRounds.AsNoTracking().SingleOrDefaultAsync(r => r.ChangeSetId == id && r.RoundNo == roundNo, cancellationToken);
        if (round is null)
        {
            return ProjectEndpoints.NotFound("Review round");
        }
        if (round.AnalysisStatus is null or AnalysisStatus.Pending)
        {
            return Conflict("Cannot analyse now", round.AnalysisStatus is null
                ? "This round is from before review analyses existed."
                : "The analysis of this round is still running.");
        }
        var now = timeProvider.GetUtcNow();
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            var reset = await db.ReviewRounds.Where(r => r.Id == round.Id && r.AnalysisStatus != AnalysisStatus.Pending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.AnalysisStatus, AnalysisStatus.Pending)
                    .SetProperty(r => r.Analysis, (string?)null)
                    .SetProperty(r => r.AnalysisError, (string?)null)
                    .SetProperty(r => r.AnalysisAt, (DateTimeOffset?)null)
                    .SetProperty(r => r.UpdatedAt, now), cancellationToken);
            if (reset == 0)
            {
                return Conflict("Cannot analyse now", "The analysis of this round is still running.");
            }
            await ReviewAnalysisJob.EnqueueAsync(jobs, round.Id, cancellationToken);
            await events.PublishAsync(ChangeSet.Topic, id, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        return TypedResults.Ok((await InfoAsync(db, id, cancellationToken))!);
    }

    /// <summary>The change set's round (the latest when none is given) and the mirror it lives in.</summary>
    private static async Task<(ReviewRound Round, string Mirror)?> RoundAsync(
        TikaiDbContext db, MirrorPaths mirrors, Guid id, int? roundNo, CancellationToken cancellationToken)
    {
        var target = await (
            from c in db.ChangeSets
            join repo in db.Repositories on c.RepositoryId equals repo.Id
            where c.Id == id
            select new { repo.ProjectId, RepositoryId = repo.Id }).SingleOrDefaultAsync(cancellationToken);
        if (target is null)
        {
            return null;
        }
        var rounds = db.ReviewRounds.AsNoTracking().Where(r => r.ChangeSetId == id);
        var round = roundNo is { } n
            ? await rounds.SingleOrDefaultAsync(r => r.RoundNo == n, cancellationToken)
            : await rounds.OrderByDescending(r => r.RoundNo).FirstOrDefaultAsync(cancellationToken);
        return round is null ? null : (round, mirrors.For(target.ProjectId, target.RepositoryId));
    }

    private static ProblemHttpResult NotFound() => ProjectEndpoints.NotFound("Change set");

    private static ProblemHttpResult Conflict(string title, string detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: title, detail: detail);
}
