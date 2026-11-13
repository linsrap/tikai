using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Tikai.Api.Attention;
using Tikai.Api.Llm;
using Tikai.Api.Projects;
using Tikai.Api.Runs;
using Tikai.Api.Tickets;
using Tikai.Core.Agents;
using Tikai.Core.Attention;
using Tikai.Core.Events;
using Tikai.Core.Git;
using Tikai.Core.Jobs;
using Tikai.Core.Projects;
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
/// The review screen's API. Diffs and file contents are computed in the mirror on every request, for a round's full
/// change or its interdiff (<see cref="ReviewDiffs"/>); blob ids come from that diff, never from the client.
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
        changeSets.MapPost("/{id:guid}/rebase", RebaseAsync);
        changeSets.MapPost("/{id:guid}/rounds/{roundNo:int}/analysis", RetryAnalysisAsync);
        changeSets.MapGet("/{id:guid}/rounds/{roundNo:int}/bundle", BundleAsync);
    }

    /// <summary>
    /// A round as a git bundle, to try it on people's own computers: its pinned round ref with the whole history, so it
    /// can be fetched into any clone of the repository (or an empty one). The review screen shows the commands.
    /// </summary>
    private static async Task<Results<FileStreamHttpResult, ProblemHttpResult>> BundleAsync(
        Guid id, int roundNo, TikaiDbContext db, IGitService git, MirrorPaths mirrors, CancellationToken cancellationToken)
    {
        var key = await (
            from c in db.ChangeSets
            join t in db.Tickets on c.TicketId equals t.Id
            join p in db.Projects on t.ProjectId equals p.Id
            where c.Id == id
            select new { p.Key, t.Number }).SingleOrDefaultAsync(cancellationToken);
        if (key is null || await RoundAsync(db, mirrors, id, roundNo, cancellationToken) is not { } target)
        {
            return NotFound();
        }
        var file = Path.Combine(Path.GetTempPath(), $"tikai-download-{Guid.NewGuid():N}.bundle");
        try
        {
            await git.CreateRefBundleAsync(target.Mirror, ReviewRules.RoundRef(id, roundNo), file, cancellationToken);
        }
        catch (GitException ex)
        {
            File.Delete(file);
            return Conflict("No bundle", ex.Message);
        }
        var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        return TypedResults.File(stream, "application/octet-stream", $"{TicketKey.Format(key.Key, key.Number)}-round-{roundNo}.bundle");
    }

    private static async Task<Results<Ok<ChangeSetInfo>, ProblemHttpResult>> GetAsync(Guid id, TikaiDbContext db, CancellationToken cancellationToken) =>
        await InfoAsync(db, id, cancellationToken) is { } info ? TypedResults.Ok(info) : NotFound();

    /// <summary>
    /// The round's changed files (default: the latest round): the interdiff since the round before, which for the first
    /// round is the full change, or with <paramref name="full"/> the whole change; with flags and the caller's viewed state.
    /// </summary>
    private static async Task<Results<Ok<ReviewFilesInfo>, ProblemHttpResult>> FilesAsync(
        Guid id, ClaimsPrincipal user, TikaiDbContext db, IGitService git, MirrorPaths mirrors, CancellationToken cancellationToken,
        int? round = null, bool full = false)
    {
        if (await RoundAsync(db, mirrors, id, round, cancellationToken) is not { } target)
        {
            return NotFound();
        }
        var shown = await ReviewDiffs.ForAsync(git, target.Mirror, target.Round, target.Previous, View(full), MaxFiles, cancellationToken);
        var diff = shown.Diff;
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
        return TypedResults.Ok(new ReviewFilesInfo(
            target.Round.RoundNo,
            target.Round.BaseSha,
            target.Round.HeadSha,
            files,
            diff.Truncated,
            shown.View,
            shown.Kind,
            shown.FromSha,
            shown.ToSha,
            target.Previous?.RoundNo,
            target.Previous?.BaseSha,
            shown.Dropped));
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

    /// <summary>Both sides of one changed file of the round's interdiff (or whole change), as text when it is text and not too large.</summary>
    private static async Task<Results<Ok<FileContentsInfo>, ProblemHttpResult>> FileAsync(
        Guid id, string path, TikaiDbContext db, IGitService git, MirrorPaths mirrors, CancellationToken cancellationToken,
        int? round = null, bool full = false)
    {
        if (await RoundAsync(db, mirrors, id, round, cancellationToken) is not { } target)
        {
            return NotFound();
        }
        var diff = (await ReviewDiffs.ForAsync(git, target.Mirror, target.Round, target.Previous, View(full), int.MaxValue, cancellationToken)).Diff;
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
        var diff = (await ReviewDiffs.ForAsync(git, target.Mirror, target.Round, target.Previous, View(request.Full), int.MaxValue, cancellationToken)).Diff;
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
    /// the ticket goes back to open. Request changes hands the "send to agent" comments to the agent and starts a fix
    /// round (<see cref="RequestChangesAsync"/>). Approve delivers the change (<see cref="ApproveAsync"/>).
    /// </summary>
    private static async Task<Results<Ok<ChangeSetInfo>, ValidationProblem, ProblemHttpResult>> VerdictAsync(
        Guid id, VerdictRequest request, ClaimsPrincipal user, TikaiDbContext db, AttentionService attention, RunControl control,
        IJobQueue jobs, IEventPublisher events, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var changeSet = await db.ChangeSets.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (changeSet is null)
        {
            return NotFound();
        }
        if (request.Verdict == ReviewVerdict.Approve)
        {
            return await ApproveAsync(changeSet, user, db, attention, control, jobs, events, timeProvider, cancellationToken);
        }
        if (request.Verdict == ReviewVerdict.RequestChanges)
        {
            return await RequestChangesAsync(changeSet, request.Message, false, user, db, attention, control, jobs, events, timeProvider, cancellationToken);
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

    /// <summary>
    /// Approves the latest round: in one transaction with the run row locked, the round gets the verdict, the change set
    /// goes open → delivering, the review and conflict items are settled, and a git job merges or pushes it
    /// (<see cref="ChangeSetDelivery"/>). The run stays in review until that is done. A container still attached is
    /// stopped.
    /// </summary>
    private static async Task<Results<Ok<ChangeSetInfo>, ValidationProblem, ProblemHttpResult>> ApproveAsync(
        ChangeSet changeSet, ClaimsPrincipal user, TikaiDbContext db, AttentionService attention, RunControl control,
        IJobQueue jobs, IEventPublisher events, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var id = changeSet.Id;
        var userId = LlmProviderService.UserId(user);
        var now = timeProvider.GetUtcNow();
        string? containerId;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            var run = await RunResumes.LockAsync(db, changeSet.RunId, cancellationToken);
            if (run is not { State: AgentRunState.InReview, CancelRequestedAt: null })
            {
                return Conflict("Not in review", "This change is not waiting for a review (it was closed, or its run ended).");
            }
            var latest = await db.ReviewRounds.Where(r => r.ChangeSetId == id).OrderByDescending(r => r.RoundNo).FirstAsync(cancellationToken);
            var decided = await db.ReviewRounds.Where(r => r.Id == latest.Id && r.Verdict == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Verdict, ReviewVerdict.Approve)
                    .SetProperty(r => r.VerdictById, userId)
                    .SetProperty(r => r.VerdictAt, now)
                    .SetProperty(r => r.UpdatedAt, now), cancellationToken);
            var delivering = decided == 0 ? 0 : await db.ChangeSets.Where(c => c.Id == id && c.Status == ChangeSetStatus.Open)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Status, ChangeSetStatus.Delivering)
                    .SetProperty(c => c.DeliveryError, (string?)null)
                    .SetProperty(c => c.UpdatedAt, now), cancellationToken);
            if (delivering == 0)
            {
                return Conflict("Already decided", "Someone gave a verdict on this round first, or the change was closed. Reload to see it.");
            }
            var settled = await db.AttentionItems
                .Where(i => i.RunId == run.Id && (i.Kind == AttentionKind.Review || i.Kind == AttentionKind.Conflict) && i.Status == AttentionStatus.Open)
                .Select(i => i.Id).ToListAsync(cancellationToken);
            foreach (var item in settled)
            {
                await attention.DismissAsync(item, userId, cancellationToken);
            }
            await db.AppendAsync(run.Id,
                [new ParsedEvent(RunEventKind.Log, new System.Text.Json.Nodes.JsonObject { ["source"] = "hub", ["text"] = $"Approved in review (round {latest.RoundNo})" })],
                null, now, cancellationToken);
            var projectId = await db.Repositories.Where(r => r.Id == changeSet.RepositoryId).Select(r => r.ProjectId).SingleAsync(cancellationToken);
            await GitJob.EnqueueAsync(jobs, GitJob.Deliver, changeSet.RepositoryId, projectId, cancellationToken, id);
            await events.PublishAsync(RunEvent.Topic, run.Id, cancellationToken);
            await events.PublishAsync(AgentRun.Topic, run.Id, cancellationToken);
            await events.PublishAsync(ChangeSet.Topic, id, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            containerId = run.ContainerId;
        }
        if (containerId is not null)
        {
            // The agent was told to stop after submitting; its clone is removed once the run ends.
            await control.StopAsync(containerId, cancellationToken);
        }
        return TypedResults.Ok((await InfoAsync(db, id, cancellationToken))!);
    }

    /// <summary>
    /// After a conflict: asks the agent to rebase onto the current default branch. A request for changes with a
    /// generated message (the conflicting files), whose resume brings the default branch into the run directory.
    /// </summary>
    private static async Task<Results<Ok<ChangeSetInfo>, ValidationProblem, ProblemHttpResult>> RebaseAsync(
        Guid id, ClaimsPrincipal user, TikaiDbContext db, AttentionService attention, RunControl control,
        IJobQueue jobs, IEventPublisher events, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var changeSet = await db.ChangeSets.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (changeSet is null)
        {
            return NotFound();
        }
        var conflict = await db.AttentionItems.AsNoTracking()
            .Where(i => i.RunId == changeSet.RunId && i.Kind == AttentionKind.Conflict && i.Status == AttentionStatus.Open)
            .OrderByDescending(i => i.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (conflict is null)
        {
            return Conflict("No conflict", "This change has no open merge conflict to rebase for.");
        }
        var payload = AttentionJson.Deserialize<ConflictPayload>(conflict.Payload);
        var files = string.Join("\n", payload.Files.Take(50).Select(f => "- " + f));
        var message = $"Round {payload.RoundNo} was approved, but it does not merge: {payload.DefaultBranch} moved since you started, "
            + $"and these files conflict:\n{files}\n\nRebase your branch onto the current {payload.DefaultBranch}, resolve the conflicts, "
            + "check that everything still works, and submit again.";
        return await RequestChangesAsync(changeSet, message, true, user, db, attention, control, jobs, events, timeProvider, cancellationToken);
    }

    /// <summary>
    /// Sends the change back to the agent: in one transaction with the run row locked, the latest round gets the
    /// verdict, the message becomes a general thread, every unsent "send to agent" comment of an open thread is marked
    /// sent, the run goes in_review → changes_requested and the ticket back to in progress, and the review item is
    /// dismissed. A container still attached (the agent's submission container) is stopped; once it is gone the fix
    /// round starts (<see cref="RunResumes"/>). Refused when nothing would reach the agent.
    /// </summary>
    /// <param name="rebase">The request asks for a rebase after a conflict (<see cref="RebaseAsync"/>).</param>
    private static async Task<Results<Ok<ChangeSetInfo>, ValidationProblem, ProblemHttpResult>> RequestChangesAsync(
        ChangeSet changeSet, string? message, bool rebase, ClaimsPrincipal user, TikaiDbContext db, AttentionService attention, RunControl control,
        IJobQueue jobs, IEventPublisher events, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        string? text = null;
        if (!string.IsNullOrWhiteSpace(message))
        {
            (text, var error) = ThreadRules.Body(message);
            if (text is null)
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["message"] = [error!] });
            }
        }
        var id = changeSet.Id;
        var userId = LlmProviderService.UserId(user);
        var now = timeProvider.GetUtcNow();
        string? containerId;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            var run = await RunResumes.LockAsync(db, changeSet.RunId, cancellationToken);
            var open = await db.ChangeSets.AnyAsync(c => c.Id == id && c.Status == ChangeSetStatus.Open, cancellationToken);
            if (!open || run is not { State: AgentRunState.InReview, CancelRequestedAt: null })
            {
                return Conflict("Not in review", "This change is not waiting for a review (it was closed, or its run ended).");
            }
            var unsent = db.ReviewComments.Where(c => c.Action == CommentAction.SendToAgent && c.SentAt == null
                && db.ReviewThreads.Any(t => t.Id == c.ThreadId && t.ChangeSetId == id && t.Status == ThreadStatus.Open));
            if (text is null && !await unsent.AnyAsync(cancellationToken))
            {
                return Conflict("Nothing to send", "Add a comment with \"Send to agent\", or a message, so the agent knows what to change.");
            }
            var latest = await db.ReviewRounds.Where(r => r.ChangeSetId == id).OrderByDescending(r => r.RoundNo).FirstAsync(cancellationToken);
            var decided = await db.ReviewRounds.Where(r => r.Id == latest.Id && r.Verdict == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Verdict, ReviewVerdict.RequestChanges)
                    .SetProperty(r => r.VerdictById, userId)
                    .SetProperty(r => r.VerdictAt, now)
                    .SetProperty(r => r.RebaseRequested, rebase)
                    .SetProperty(r => r.UpdatedAt, now), cancellationToken);
            if (decided == 0)
            {
                return Conflict("Already decided", "Someone gave a verdict on this round first. Reload to see it.");
            }
            await unsent.ExecuteUpdateAsync(s => s.SetProperty(c => c.SentAt, now).SetProperty(c => c.UpdatedAt, now), cancellationToken);
            if (text is not null)
            {
                var thread = new ReviewThread { ChangeSetId = id, ReviewRoundId = latest.Id, Status = ThreadStatus.Open, CreatedById = userId };
                db.ReviewThreads.Add(thread);
                await db.SaveChangesAsync(cancellationToken);
                db.ReviewComments.Add(new ReviewComment
                {
                    ThreadId = thread.Id,
                    AuthorUserId = userId,
                    Body = text,
                    Action = CommentAction.SendToAgent,
                    SentAt = now,
                });
                await db.SaveChangesAsync(cancellationToken);
            }
            await db.AgentRuns.Where(r => r.Id == run.Id && r.State == AgentRunState.InReview)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.State, AgentRunState.ChangesRequested).SetProperty(r => r.UpdatedAt, now), cancellationToken);
            await db.Tickets.Where(t => t.Id == run.TicketId && t.Status == TicketStatus.InReview)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TicketStatus.InProgress).SetProperty(t => t.UpdatedAt, now), cancellationToken);
            var reviewItems = await db.AttentionItems
                .Where(i => i.RunId == run.Id && (i.Kind == AttentionKind.Review || i.Kind == AttentionKind.Conflict) && i.Status == AttentionStatus.Open)
                .Select(i => i.Id).ToListAsync(cancellationToken);
            foreach (var item in reviewItems)
            {
                await attention.DismissAsync(item, userId, cancellationToken);
            }
            var note = rebase ? $"Rebase requested after a conflict (round {latest.RoundNo})" : $"Changes requested in review (round {latest.RoundNo})";
            await db.AppendAsync(run.Id,
                [new ParsedEvent(RunEventKind.Log, new System.Text.Json.Nodes.JsonObject { ["source"] = "hub", ["text"] = note })],
                null, now, cancellationToken);
            // Without a container the fix round starts now; otherwise when the container's end is recorded.
            await RunResumes.RequestIfReadyAsync(db, jobs, run.Id, cancellationToken);
            await events.PublishAsync(RunEvent.Topic, run.Id, cancellationToken);
            await events.PublishAsync(AgentRun.Topic, run.Id, cancellationToken);
            await events.PublishAsync(ChangeSet.Topic, id, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            containerId = run.ContainerId;
        }
        if (containerId is not null)
        {
            // The agent was told to stop after submitting; one that keeps going would hold up the fix round.
            await control.StopAsync(containerId, cancellationToken);
        }
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
            select new { ChangeSet = c, Run = r, TicketNumber = t.Number, t.Title, p.Slug, p.Key, p.Settings, RepositoryName = repo.Name, repo.DefaultBranch, repo.RemoteUrl })
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }
        var criteria = await db.AcceptanceCriteria.AsNoTracking().Where(c => c.TicketId == row.ChangeSet.TicketId)
            .OrderBy(c => c.Position).ToListAsync(cancellationToken);
        var texts = criteria.ToDictionary(c => c.Id, c => c.Text);
        var rounds = await db.ReviewRounds.AsNoTracking().Where(r => r.ChangeSetId == id).OrderBy(r => r.RoundNo).ToListAsync(cancellationToken);
        var delivery = ProjectSettings.Parse(row.Settings).DeliveryOrDefault;
        var conflict = await db.AttentionItems.AsNoTracking()
            .Where(i => i.RunId == row.Run.Id && i.Kind == AttentionKind.Conflict && i.Status == AttentionStatus.Open)
            .OrderByDescending(i => i.CreatedAt).FirstOrDefaultAsync(cancellationToken);
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
            row.ChangeSet.CreatedAt,
            new DeliveryInfo(
                delivery.Mode ?? DeliveryMode.Merge,
                delivery.MergeStyle ?? MergeStyle.Squash,
                row.RemoteUrl is not null,
                row.ChangeSet.DeliveredSha,
                row.ChangeSet.DeliveryError,
                conflict is null ? null : new ConflictInfo(conflict.Id, AttentionJson.Deserialize<ConflictPayload>(conflict.Payload).Files)),
            (await RunPlans.ApprovedAsync(db, [row.Run.Id], cancellationToken)).Count > 0);
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

    /// <summary>The change set's round (the latest when none is given), the round before it, and the mirror they live in.</summary>
    internal static async Task<(ReviewRound Round, ReviewRound? Previous, string Mirror)?> RoundAsync(
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
        if (round is null)
        {
            return null;
        }
        var previous = await rounds.SingleOrDefaultAsync(r => r.RoundNo == round.RoundNo - 1, cancellationToken);
        return (round, previous, mirrors.For(target.ProjectId, target.RepositoryId));
    }

    private static ReviewView View(bool full) => full ? ReviewView.Full : ReviewView.Interdiff;

    private static ProblemHttpResult NotFound() => ProjectEndpoints.NotFound("Change set");

    private static ProblemHttpResult Conflict(string title, string detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: title, detail: detail);
}
