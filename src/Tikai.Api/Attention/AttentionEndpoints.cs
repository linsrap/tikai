using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tikai.Api.Llm;
using Tikai.Api.Runs;
using Tikai.Core.Attention;
using Tikai.Core.Reviews;
using Tikai.Core.Runs;
using Tikai.Core.Tickets;
using Tikai.Data;
using Tikai.Orchestrator;

namespace Tikai.Api.Attention;

/// <summary>The inbox's API: open items, answering questions and plans, dismissing and retrying the rest.</summary>
internal static class AttentionEndpoints
{
    /// <summary>Transcript events shown with a question or plan: what the agent did right before it asked.</summary>
    private const int ExcerptLength = 5;

    /// <summary>Transcript events shown with a failure: how the run ended.</summary>
    private const int FailureExcerptLength = 8;

    private static readonly RunEventKind[] _excerptKinds =
        [RunEventKind.Message, RunEventKind.ToolCall, RunEventKind.ToolResult, RunEventKind.Progress, RunEventKind.Log];

    public static void MapAttentionEndpoints(this RouteGroupBuilder api)
    {
        var items = api.MapGroup("/attention-items").WithTags("Attention").RequireAuthorization();
        items.MapGet("/", ListOpenAsync);
        items.MapGet("/{id:guid}", GetAsync);
        items.MapPost("/{id:guid}/answer", AnswerAsync);
        items.MapPost("/{id:guid}/dismiss", DismissAsync);
        items.MapPost("/{id:guid}/retry", RetryAsync);
    }

    /// <summary>
    /// Open items of all projects: those an agent waits for first, then the rest, each newest first. An archived
    /// project's items (failures; its runs were cancelled) stay out until it is restored.
    /// </summary>
    private static async Task<Ok<List<AttentionItemInfo>>> ListOpenAsync(TikaiDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await QueryAsync(
            db,
            db.AttentionItems.Where(i => i.Status == AttentionStatus.Open && db.Tickets.Any(
                t => t.Id == i.TicketId && db.Projects.Any(p => p.Id == t.ProjectId && p.ArchivedAt == null && p.DeletingAt == null))),
            cancellationToken));

    private static async Task<Results<Ok<AttentionItemInfo>, ProblemHttpResult>> GetAsync(Guid id, TikaiDbContext db, CancellationToken cancellationToken) =>
        (await QueryAsync(db, db.AttentionItems.Where(i => i.Id == id), cancellationToken)) is [var item]
            ? TypedResults.Ok(item)
            : NotFound();

    /// <summary>Answers a question or decides on a plan. The waiting agent gets the answer as its tool result.</summary>
    private static async Task<Results<Ok<AttentionItemInfo>, ValidationProblem, ProblemHttpResult>> AnswerAsync(
        Guid id, AnswerRequest request, ClaimsPrincipal user, TikaiDbContext db, AttentionService attention, CancellationToken cancellationToken)
    {
        var item = await db.AttentionItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item is null)
        {
            return NotFound();
        }
        if (item.Kind is not (AttentionKind.Question or AttentionKind.PlanApproval))
        {
            return Conflict("This item is not answered here", "Only questions and plan approvals take an answer.");
        }
        if (item.Status == AttentionStatus.Dismissed)
        {
            return Conflict("The run has ended", "This question no longer reaches an agent: its run failed or was cancelled.");
        }
        var (answer, error) = AttentionRules.Answer(item.Kind, request.Text, request.Decision);
        if (error is var (field, message))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
        }
        if (!await attention.AnswerAsync(id, answer!, LlmProviderService.UserId(user), cancellationToken))
        {
            return Conflict("The item is already answered", "Someone answered it first. Reload to see the answer.");
        }
        return TypedResults.Ok((await QueryAsync(db, db.AttentionItems.Where(i => i.Id == id), cancellationToken))[0]);
    }

    /// <summary>
    /// Closes a failure, a merge conflict or a submitted review without acting on it. Questions and plans are not
    /// dismissed: an agent waits for them (cancel the run instead). A dismissed conflict leaves the change open for a
    /// verdict on the review screen. A review is dismissed only when its change set takes no verdict (none, or not open).
    /// </summary>
    private static async Task<Results<Ok<AttentionItemInfo>, ProblemHttpResult>> DismissAsync(
        Guid id, ClaimsPrincipal user, TikaiDbContext db, AttentionService attention, CancellationToken cancellationToken)
    {
        var item = await db.AttentionItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item is null)
        {
            return NotFound();
        }
        if (item.Kind.IsBlocking())
        {
            return Conflict("An agent waits for this item", "Answer it, or cancel the run if it should stop.");
        }
        if (item.Kind == AttentionKind.Review && await db.ChangeSets.AnyAsync(c => c.RunId == item.RunId && c.Status == ChangeSetStatus.Open, cancellationToken))
        {
            return Conflict("The work waits for a review", "Open the review and give a verdict instead.");
        }
        if (!await attention.DismissAsync(id, LlmProviderService.UserId(user), cancellationToken))
        {
            return Conflict("The item is not open", "It was dismissed or retried already. Reload to see it.");
        }
        return TypedResults.Ok((await QueryAsync(db, db.AttentionItems.Where(i => i.Id == id), cancellationToken))[0]);
    }

    /// <summary>
    /// Starts a new run of a failed run's ticket, with the same repository and provider. The failure item is dismissed
    /// in the same transaction (as all open failures of the ticket are when a new run starts).
    /// </summary>
    private static async Task<Results<Accepted<RunInfo>, ValidationProblem, ProblemHttpResult>> RetryAsync(
        Guid id, ClaimsPrincipal user, TikaiDbContext db, RunService runs, IOptions<OrchestratorOptions> options, CancellationToken cancellationToken)
    {
        var item = await db.AttentionItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item is null)
        {
            return NotFound();
        }
        if (item.Kind != AttentionKind.Failure || item.RunId is null)
        {
            return Conflict("Only a failed run is retried", "Retry is for failure items.");
        }
        if (item.Status != AttentionStatus.Open)
        {
            return Conflict("The item is not open", "It was dismissed or retried already. Reload to see it.");
        }
        var failed = await db.AgentRuns.AsNoTracking().SingleAsync(r => r.Id == item.RunId, cancellationToken);
        var ticket = await db.Tickets.AsNoTracking().SingleAsync(t => t.Id == item.TicketId, cancellationToken);
        var project = await db.Projects.AsNoTracking().SingleAsync(p => p.Id == ticket.ProjectId, cancellationToken);

        var (run, problem) = await runs.StartAsync(project, ticket, failed.RepositoryId, failed.LlmProviderId, LlmProviderService.UserId(user), cancellationToken);
        if (problem is not null)
        {
            // The failed run's repository or provider may be gone (or the provider is someone else's own).
            return problem.Field is null
                ? TypedResults.Problem(statusCode: problem.Status, title: problem.Title, detail: problem.Detail)
                : Conflict(problem.Title, problem.Detail + " Start the run from the ticket page instead.");
        }
        var info = (await RunEndpoints.QueryAsync(db, options, db.AgentRuns.Where(r => r.Id == run!.Id), cancellationToken)).Single();
        return TypedResults.Accepted($"/api/runs/{info.Id}", info);
    }

    private static async Task<List<AttentionItemInfo>> QueryAsync(TikaiDbContext db, IQueryable<AttentionItem> items, CancellationToken cancellationToken)
    {
        var rows = await items.AsNoTracking()
            .Join(db.Tickets, i => i.TicketId, t => t.Id, (i, t) => new { Item = i, TicketId = t.Id, t.Number, t.Title, t.ProjectId })
            .Join(db.Projects, x => x.ProjectId, p => p.Id, (x, p) => new { x.Item, x.TicketId, x.Number, x.Title, p.Slug, p.Key })
            .ToListAsync(cancellationToken);

        var runIds = rows.Where(x => x.Item.RunId is not null).Select(x => x.Item.RunId!.Value).Distinct().ToList();
        var runs = await db.AgentRuns.AsNoTracking()
            .Where(r => runIds.Contains(r.Id))
            .Select(r => new { r.Id, Run = new ItemRun(r.State, r.Adapter, r.Model, r.ProgressStep, r.ProgressNote, r.Attempt) })
            .ToDictionaryAsync(r => r.Id, r => r.Run, cancellationToken);
        var changeSetRows = await db.ChangeSets.AsNoTracking()
            .Where(c => runIds.Contains(c.RunId))
            .Select(c => new { c.RunId, c.Id, c.Status, c.DeliveryError })
            .ToListAsync(cancellationToken);
        var changeSets = changeSetRows.ToDictionary(c => c.RunId, c => c.Id);
        var changeSetStatuses = changeSetRows.ToDictionary(c => c.RunId, c => c.Status);
        var deliveryErrors = changeSetRows.Where(c => c.Status == ChangeSetStatus.Open).ToDictionary(c => c.RunId, c => c.DeliveryError);
        var changeSetIds = changeSets.Values.ToList();
        var latest = await db.ReviewRounds.AsNoTracking()
            .Where(r => changeSetIds.Contains(r.ChangeSetId) && r.RoundNo == db.ReviewRounds.Where(o => o.ChangeSetId == r.ChangeSetId).Max(o => o.RoundNo))
            .ToListAsync(cancellationToken);
        var latestRounds = latest.ToDictionary(r => r.ChangeSetId);
        // Threads the agent was asked to act on, and whether it replied after the last comment sent to it.
        var sentThreads = await db.ReviewThreads.AsNoTracking()
            .Where(t => changeSetIds.Contains(t.ChangeSetId) && db.ReviewComments.Any(c => c.ThreadId == t.Id && c.SentAt != null))
            .Select(t => new
            {
                t.ChangeSetId,
                Answered = !db.ReviewComments.Any(c => c.ThreadId == t.Id && c.SentAt != null
                    && !db.ReviewComments.Any(r => r.ThreadId == t.Id && r.AuthorRunId != null && r.CreatedAt > c.CreatedAt)),
            })
            .ToListAsync(cancellationToken);
        var roundBriefs = latest.ToDictionary(r => r.ChangeSetId, r => new RoundBrief(
            r.RoundNo,
            sentThreads.Count(t => t.ChangeSetId == r.ChangeSetId && t.Answered),
            sentThreads.Count(t => t.ChangeSetId == r.ChangeSetId && !t.Answered)));

        var criterionIds = rows.Where(x => x.Item.Kind == AttentionKind.Review)
            .SelectMany(x => AttentionJson.Deserialize<ReviewPayload>(x.Item.Payload).CriteriaMapping.Select(m => m.CriterionId))
            .Distinct().ToList();
        var criteria = criterionIds.Count == 0
            ? []
            : await db.AcceptanceCriteria.AsNoTracking().Where(c => criterionIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Text, cancellationToken);
        var reviewedTickets = rows.Where(x => x.Item.Kind == AttentionKind.Review).Select(x => x.TicketId).Distinct().ToList();
        var criteriaCounts = reviewedTickets.Count == 0
            ? []
            : await db.AcceptanceCriteria.AsNoTracking().Where(c => reviewedTickets.Contains(c.TicketId))
                .GroupBy(c => c.TicketId).Select(g => new { TicketId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.TicketId, g => g.Count, cancellationToken);

        var planned = await RunPlans.ApprovedAsync(db, runIds, cancellationToken);

        var result = new List<AttentionItemInfo>(rows.Count);
        foreach (var x in rows)
        {
            var item = x.Item;
            var review = item.Kind == AttentionKind.Review ? AttentionJson.Deserialize<ReviewPayload>(item.Payload) : null;
            result.Add(new AttentionItemInfo(
                item.Id,
                item.Kind,
                item.Status,
                item.Status == AttentionStatus.Open && item.Kind.IsBlocking(),
                item.RunId,
                x.Slug,
                x.Number,
                TicketKey.Format(x.Key, x.Number),
                x.Title,
                item.RunId is { } runId && runs.TryGetValue(runId, out var run) ? run : null,
                item.Kind == AttentionKind.Question ? AttentionJson.Deserialize<QuestionPayload>(item.Payload) : null,
                item.Kind == AttentionKind.PlanApproval ? AttentionJson.Deserialize<PlanPayload>(item.Payload) : null,
                review,
                review is null ? [] : [.. review.CriteriaMapping.Select(m => new ReviewCoverage(m.CriterionId, criteria.GetValueOrDefault(m.CriterionId), m.Status, m.Note))],
                item.Kind == AttentionKind.Failure ? AttentionJson.Deserialize<FailurePayload>(item.Payload) : null,
                AttentionService.AnswerOf(item),
                await ExcerptAsync(db, item, cancellationToken),
                item.CreatedAt,
                item.AnsweredAt,
                item.Kind is AttentionKind.Review or AttentionKind.Conflict && item.RunId is { } reviewed && changeSets.TryGetValue(reviewed, out var changeSetId)
                    ? changeSetId : null,
                item.Kind == AttentionKind.Review && item.RunId is { } analysed && changeSets.TryGetValue(analysed, out var analysedSet)
                    && latestRounds.TryGetValue(analysedSet, out var analysedRound)
                    ? Brief(analysedRound, criteriaCounts.GetValueOrDefault(x.TicketId)) : null,
                item.Kind == AttentionKind.Review && item.RunId is { } rounded && changeSets.TryGetValue(rounded, out var roundedSet)
                    ? roundBriefs.GetValueOrDefault(roundedSet) : null,
                item.Kind == AttentionKind.Conflict ? AttentionJson.Deserialize<ConflictPayload>(item.Payload) : null,
                item.Kind == AttentionKind.Review && item.Status == AttentionStatus.Open && item.RunId is { } delivered
                    ? deliveryErrors.GetValueOrDefault(delivered) : null,
                item.Kind is AttentionKind.Review or AttentionKind.Conflict && item.RunId is { } changed && changeSetStatuses.TryGetValue(changed, out var changeSetStatus)
                    ? changeSetStatus : null,
                item.Kind == AttentionKind.Review && item.RunId is { } reviewedRun ? planned.Contains(reviewedRun) : null));
        }
        return [.. result.OrderByDescending(i => i.Blocking).ThenByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id)];
    }

    private static AnalysisBrief? Brief(ReviewRound round, int criteria)
    {
        if (round.AnalysisStatus is not { } status)
        {
            return null;
        }
        var analysis = round.Analysis is { } json ? AttentionJson.Deserialize<ReviewAnalysis>(json) : null;
        int Count(CriterionCoverage coverage) => analysis?.Coverage.Count(c => c.Status == coverage) ?? 0;
        return new AnalysisBrief(
            status,
            Count(CriterionCoverage.Covered),
            Count(CriterionCoverage.Partial),
            Count(CriterionCoverage.Missing),
            analysis?.Risks.Count(r => r.Risk == FileRisk.High) ?? 0,
            round.AnalysisError,
            criteria,
            analysis is null ? 0 : CloseLook(analysis),
            analysis?.Headline,
            analysis?.LookAt);
    }

    /// <summary>Files the analysis points at or rates high risk, each once.</summary>
    private static int CloseLook(ReviewAnalysis analysis) =>
        (analysis.LookAt ?? []).Select(l => l.Path)
            .Concat(analysis.Risks.Where(r => r.Risk == FileRisk.High).Select(r => r.Path))
            .Distinct(StringComparer.Ordinal)
            .Count();

    /// <summary>What the agent did right before it raised the item; for a failure, how the run ended.</summary>
    private static async Task<IReadOnlyList<RunEventInfo>> ExcerptAsync(TikaiDbContext db, AttentionItem item, CancellationToken cancellationToken)
    {
        if (item.RunId is not { } runId)
        {
            return [];
        }
        var events = db.RunEvents.AsNoTracking().Where(e => e.RunId == runId);
        var picked = item.Kind == AttentionKind.Failure
            ? events.OrderByDescending(e => e.Seq).Take(FailureExcerptLength)
            : events.Where(e => e.CreatedAt <= item.CreatedAt && _excerptKinds.Contains(e.Kind)).OrderByDescending(e => e.Seq).Take(ExcerptLength);
        var rows = await picked.ToListAsync(cancellationToken);
        return [.. rows.OrderBy(e => e.Seq).Select(RunEndpoints.ToInfo)];
    }

    private static ProblemHttpResult Conflict(string title, string detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: title, detail: detail);

    private static ProblemHttpResult NotFound() =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Attention item not found");
}
