using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Tikai.Api.Llm;
using Tikai.Api.Projects;
using Tikai.Core.Events;
using Tikai.Core.Git;
using Tikai.Core.Reviews;
using Tikai.Data;
using Tikai.Review;

namespace Tikai.Api.Reviews;

/// <summary>
/// Review threads and comments. Comments are saved at once and visible to every logged-in user; a "send to agent"
/// comment reaches the agent only with the next request for changes, and until then its author can change or delete it.
/// Threads are resolved and reopened by people. Nothing changes once the change set is closed or merged.
/// </summary>
internal static class ThreadEndpoints
{
    public static void MapThreadEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/change-sets/{id:guid}/threads", ListAsync).WithTags("Reviews").RequireAuthorization();
        api.MapPost("/change-sets/{id:guid}/threads", CreateAsync).WithTags("Reviews").RequireAuthorization();

        var threads = api.MapGroup("/review-threads").WithTags("Reviews").RequireAuthorization();
        threads.MapPost("/{id:guid}/comments", ReplyAsync);
        threads.MapPost("/{id:guid}/resolve", (Guid id, ClaimsPrincipal user, TikaiDbContext db, IEventPublisher events, TimeProvider time, CancellationToken ct) =>
            SetStatusAsync(id, ThreadStatus.Resolved, user, db, events, time, ct));
        threads.MapPost("/{id:guid}/reopen", (Guid id, ClaimsPrincipal user, TikaiDbContext db, IEventPublisher events, TimeProvider time, CancellationToken ct) =>
            SetStatusAsync(id, ThreadStatus.Open, user, db, events, time, ct));

        var comments = api.MapGroup("/review-comments").WithTags("Reviews").RequireAuthorization();
        comments.MapPatch("/{id:guid}", EditAsync);
        comments.MapDelete("/{id:guid}", DeleteAsync);
    }

    /// <summary>Every thread of the change set with its comments, oldest first.</summary>
    private static async Task<Results<Ok<IReadOnlyList<ReviewThreadInfo>>, ProblemHttpResult>> ListAsync(
        Guid id, ClaimsPrincipal user, TikaiDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.ChangeSets.AnyAsync(c => c.Id == id, cancellationToken))
        {
            return ProjectEndpoints.NotFound("Change set");
        }
        return TypedResults.Ok(await InfosAsync(db, id, null, LlmProviderService.UserId(user), cancellationToken));
    }

    /// <summary>
    /// Starts a thread on a round: anchored to lines of one side of a file (the round's base is the old side, its head the
    /// new side), or general. The file must be a text file on that side and the lines within it.
    /// </summary>
    private static async Task<Results<Ok<ReviewThreadInfo>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        Guid id, CreateThreadRequest request, ClaimsPrincipal user, TikaiDbContext db, IGitService git, MirrorPaths mirrors,
        IEventPublisher events, CancellationToken cancellationToken)
    {
        var (body, bodyError) = ThreadRules.Body(request.Body);
        var (anchor, anchorError) = request.Path is { Length: > ReviewRules.MaxFilePathLength }
            ? (null, $"A path has at most {ReviewRules.MaxFilePathLength} characters.")
            : ThreadRules.Anchor(request.Path, request.Side, request.LineStart, request.LineEnd);
        if (bodyError is not null || anchorError is not null)
        {
            return Invalid(bodyError is not null ? "body" : "path", bodyError ?? anchorError!);
        }
        if (await ChangeSetEndpoints.RoundAsync(db, mirrors, id, request.RoundNo, cancellationToken) is not { } target)
        {
            return ProjectEndpoints.NotFound("Review round");
        }
        if (await ClosedAsync(db, id, cancellationToken) is { } closed)
        {
            return closed;
        }
        if (anchor is not null && await CheckAnchorAsync(git, target.Mirror, target.Round, anchor, cancellationToken) is { } problem)
        {
            return Invalid("path", problem);
        }

        var userId = LlmProviderService.UserId(user);
        var thread = new ReviewThread
        {
            ChangeSetId = id,
            ReviewRoundId = target.Round.Id,
            FilePath = anchor?.Path,
            Side = anchor?.Side,
            LineStart = anchor?.LineStart,
            LineEnd = anchor?.LineEnd,
            Status = ThreadStatus.Open,
            CreatedById = userId,
        };
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.ReviewThreads.Add(thread);
        await db.SaveChangesAsync(cancellationToken);
        db.ReviewComments.Add(new ReviewComment { ThreadId = thread.Id, AuthorUserId = userId, Body = body!, Action = request.Action });
        await db.SaveChangesAsync(cancellationToken);
        await events.PublishAsync(ChangeSet.Topic, id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Ok((await InfosAsync(db, id, thread.Id, userId, cancellationToken)).Single());
    }

    private static async Task<Results<Ok<ReviewThreadInfo>, ValidationProblem, ProblemHttpResult>> ReplyAsync(
        Guid id, CommentRequest request, ClaimsPrincipal user, TikaiDbContext db, IEventPublisher events, CancellationToken cancellationToken)
    {
        var (body, error) = ThreadRules.Body(request.Body);
        if (body is null)
        {
            return Invalid("body", error!);
        }
        if (await db.ReviewThreads.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, cancellationToken) is not { } thread)
        {
            return ProjectEndpoints.NotFound("Review thread");
        }
        if (await ClosedAsync(db, thread.ChangeSetId, cancellationToken) is { } closed)
        {
            return closed;
        }
        var userId = LlmProviderService.UserId(user);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.ReviewComments.Add(new ReviewComment { ThreadId = id, AuthorUserId = userId, Body = body, Action = request.Action });
        await db.SaveChangesAsync(cancellationToken);
        await events.PublishAsync(ChangeSet.Topic, thread.ChangeSetId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Ok((await InfosAsync(db, thread.ChangeSetId, id, userId, cancellationToken)).Single());
    }

    private static async Task<Results<Ok<ReviewThreadInfo>, ProblemHttpResult>> SetStatusAsync(
        Guid id, ThreadStatus status, ClaimsPrincipal user, TikaiDbContext db, IEventPublisher events, TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (await db.ReviewThreads.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, cancellationToken) is not { } thread)
        {
            return ProjectEndpoints.NotFound("Review thread");
        }
        if (await ClosedAsync(db, thread.ChangeSetId, cancellationToken) is { } closed)
        {
            return closed;
        }
        var userId = LlmProviderService.UserId(user);
        var now = timeProvider.GetUtcNow();
        var resolved = status == ThreadStatus.Resolved;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.ReviewThreads.Where(t => t.Id == id && t.Status != status)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, status)
                .SetProperty(t => t.ResolvedById, resolved ? userId : null)
                .SetProperty(t => t.ResolvedAt, resolved ? now : null)
                .SetProperty(t => t.UpdatedAt, now), cancellationToken);
        await events.PublishAsync(ChangeSet.Topic, thread.ChangeSetId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Ok((await InfosAsync(db, thread.ChangeSetId, id, userId, cancellationToken)).Single());
    }

    /// <summary>Changes the body or the action of one's own comment that was not sent to the agent yet.</summary>
    private static async Task<Results<Ok<ReviewThreadInfo>, ValidationProblem, ProblemHttpResult>> EditAsync(
        Guid id, CommentRequest request, ClaimsPrincipal user, TikaiDbContext db, IEventPublisher events, TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var (body, error) = ThreadRules.Body(request.Body);
        if (body is null)
        {
            return Invalid("body", error!);
        }
        var userId = LlmProviderService.UserId(user);
        if (await OwnUnsentAsync(db, id, userId, cancellationToken) is not { } found)
        {
            return CannotChange();
        }
        if (await ClosedAsync(db, found.ChangeSetId, cancellationToken) is { } closed)
        {
            return closed;
        }
        var now = timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // The request for changes may have sent it meanwhile.
        var changed = await db.ReviewComments.Where(c => c.Id == id && c.SentAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Body, body)
                .SetProperty(c => c.Action, request.Action)
                .SetProperty(c => c.EditedAt, now)
                .SetProperty(c => c.UpdatedAt, now), cancellationToken);
        if (changed == 0)
        {
            return CannotChange();
        }
        await events.PublishAsync(ChangeSet.Topic, found.ChangeSetId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Ok((await InfosAsync(db, found.ChangeSetId, found.ThreadId, userId, cancellationToken)).Single());
    }

    /// <summary>Deletes one's own unsent comment; a thread left without comments goes with it.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid id, ClaimsPrincipal user, TikaiDbContext db, IEventPublisher events, CancellationToken cancellationToken)
    {
        var userId = LlmProviderService.UserId(user);
        if (await OwnUnsentAsync(db, id, userId, cancellationToken) is not { } found)
        {
            return CannotChange();
        }
        if (await ClosedAsync(db, found.ChangeSetId, cancellationToken) is { } closed)
        {
            return closed;
        }
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await db.ReviewComments.Where(c => c.Id == id && c.SentAt == null).ExecuteDeleteAsync(cancellationToken) == 0)
        {
            return CannotChange();
        }
        await db.ReviewThreads.Where(t => t.Id == found.ThreadId && !db.ReviewComments.Any(c => c.ThreadId == t.Id)).ExecuteDeleteAsync(cancellationToken);
        await events.PublishAsync(ChangeSet.Topic, found.ChangeSetId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>The threads of a change set (or one of them), with comments and the names of their authors.</summary>
    internal static async Task<IReadOnlyList<ReviewThreadInfo>> InfosAsync(
        TikaiDbContext db, Guid changeSetId, Guid? threadId, Guid userId, CancellationToken cancellationToken)
    {
        var threads = await (
            from t in db.ReviewThreads.AsNoTracking()
            join r in db.ReviewRounds on t.ReviewRoundId equals r.Id
            where t.ChangeSetId == changeSetId && (threadId == null || t.Id == threadId)
            orderby t.CreatedAt, t.Id
            select new { Thread = t, r.RoundNo }).ToListAsync(cancellationToken);
        var ids = threads.Select(t => t.Thread.Id).ToList();
        var comments = await db.ReviewComments.AsNoTracking().Where(c => ids.Contains(c.ThreadId))
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToListAsync(cancellationToken);
        var userIds = comments.Select(c => c.AuthorUserId).Concat(threads.Select(t => t.Thread.ResolvedById)).OfType<Guid>().Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);
        var byThread = comments.ToLookup(c => c.ThreadId);
        return [.. threads.Select(t => new ReviewThreadInfo(
            t.Thread.Id,
            t.RoundNo,
            t.Thread.FilePath,
            t.Thread.Side,
            t.Thread.LineStart,
            t.Thread.LineEnd,
            t.Thread.Status,
            t.Thread.CreatedAt,
            t.Thread.ResolvedById is { } by ? names.GetValueOrDefault(by) : null,
            t.Thread.ResolvedAt,
            [.. byThread[t.Thread.Id].Select(c => new ReviewCommentInfo(
                c.Id,
                c.AuthorRunId is null ? CommentAuthorKind.Person : CommentAuthorKind.Agent,
                c.AuthorUserId is { } author ? names.GetValueOrDefault(author) ?? "Unknown" : "Agent",
                c.Body,
                c.Action,
                c.CreatedAt,
                c.EditedAt,
                c.SentAt,
                CanEdit: c.AuthorUserId == userId && c.SentAt is null))]))];
    }

    /// <summary>
    /// Whether an anchor fits the round: a text file on that side (base for old, head for new), with the lines inside it.
    /// Files too large to show are not checked further. Null when it fits, else why not.
    /// </summary>
    private static async Task<string?> CheckAnchorAsync(IGitService git, string mirror, ReviewRound round, ThreadAnchor anchor, CancellationToken cancellationToken)
    {
        var commit = anchor.Side == DiffSide.New ? round.HeadSha : round.BaseSha;
        if (await git.BlobIdAsync(mirror, commit, anchor.Path, cancellationToken) is not { } blob
            || await git.ReadBlobAsync(mirror, blob, FileClassifier.MaxShownBytes, cancellationToken) is not { } content)
        {
            return $"{anchor.Path} does not exist on the {(anchor.Side == DiffSide.New ? "new" : "old")} side of round {round.RoundNo}.";
        }
        if (content.Content is null)
        {
            return null;
        }
        if (FileText.Decode(content.Content) is not { } text)
        {
            return "Comments go on lines of text files; use a general comment for a binary file.";
        }
        var lines = text.Length == 0 ? 0 : text.Count(c => c == '\n') + (text.EndsWith('\n') ? 0 : 1);
        return anchor.LineEnd > lines ? $"{anchor.Path} has {lines} line{(lines == 1 ? "" : "s")} on that side." : null;
    }

    private static async Task<(Guid ChangeSetId, Guid ThreadId)?> OwnUnsentAsync(TikaiDbContext db, Guid commentId, Guid userId, CancellationToken cancellationToken)
    {
        var found = await (
            from c in db.ReviewComments
            join t in db.ReviewThreads on c.ThreadId equals t.Id
            where c.Id == commentId && c.AuthorUserId == userId && c.SentAt == null
            select new { t.ChangeSetId, ThreadId = t.Id }).SingleOrDefaultAsync(cancellationToken);
        return found is null ? null : (found.ChangeSetId, found.ThreadId);
    }

    private static async Task<ProblemHttpResult?> ClosedAsync(TikaiDbContext db, Guid changeSetId, CancellationToken cancellationToken) =>
        await db.ChangeSets.AnyAsync(c => c.Id == changeSetId && c.Status == ChangeSetStatus.Open, cancellationToken)
            ? null
            : TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Review closed", detail: "This change is closed; its comments cannot change any more.");

    private static ProblemHttpResult CannotChange() =>
        TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Cannot change this comment",
            detail: "Only your own comments can be changed, and only until they are sent to the agent.");

    private static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
