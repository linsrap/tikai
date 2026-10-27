using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tikai.Api.Llm;
using Tikai.Api.Projects;
using Tikai.Core.Attention;
using Tikai.Core.Runs;
using Tikai.Data;
using Tikai.Orchestrator;

namespace Tikai.Api.Runs;

internal static class RunEndpoints
{
    private const int MaxEventsPerPage = 1_000;

    /// <summary>How long an ended run stays on the agent board.</summary>
    private static readonly TimeSpan _recentlyEnded = TimeSpan.FromHours(1);

    public static void MapRunEndpoints(this RouteGroupBuilder api)
    {
        var ticketRuns = api.MapGroup("/projects/{slug}/tickets/{number:int}/runs").WithTags("Runs").RequireAuthorization();
        ticketRuns.MapPost("/", StartAsync);
        ticketRuns.MapGet("/", ListAsync);

        var runs = api.MapGroup("/runs").WithTags("Runs").RequireAuthorization();
        runs.MapGet("/", ListActiveAsync);
        runs.MapGet("/{id:guid}", GetAsync);
        runs.MapGet("/{id:guid}/events", EventsAsync);
        runs.MapPost("/{id:guid}/cancel", CancelAsync);
    }

    /// <summary>
    /// Assigns the ticket to the agent: a queued run on its own branch, started by the orchestrator. The ticket moves to
    /// in_progress and its open failure items are dismissed. One active run per ticket and per repository.
    /// </summary>
    private static async Task<Results<Accepted<RunInfo>, ValidationProblem, ProblemHttpResult>> StartAsync(
        string slug,
        int number,
        StartRunRequest request,
        ClaimsPrincipal user,
        TikaiDbContext db,
        RunService runs,
        IOptions<OrchestratorOptions> options,
        CancellationToken cancellationToken)
    {
        var project = await ProjectEndpoints.FindAsync(db, slug, cancellationToken);
        if (project is null)
        {
            return ProjectEndpoints.NotFound();
        }
        var ticket = await db.Tickets.AsNoTracking().SingleOrDefaultAsync(t => t.ProjectId == project.Id && t.Number == number, cancellationToken);
        if (ticket is null)
        {
            return ProjectEndpoints.NotFound("Ticket");
        }

        var (run, problem) = await runs.StartAsync(project, ticket, request.RepositoryId, request.ProviderId, LlmProviderService.UserId(user), cancellationToken);
        if (problem is not null)
        {
            return problem.Field is { } field
                ? TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [problem.Detail] })
                : TypedResults.Problem(statusCode: problem.Status, title: problem.Title, detail: problem.Detail);
        }
        var info = (await QueryAsync(db, options, db.AgentRuns.Where(r => r.Id == run!.Id), cancellationToken)).Single();
        return TypedResults.Accepted($"/api/runs/{info.Id}", info);
    }

    /// <summary>
    /// For the agent board: every run that has not ended, plus those that ended within the last hour (so a run that
    /// just finished does not vanish while someone watches it), newest first.
    /// </summary>
    private static async Task<Ok<List<RunInfo>>> ListActiveAsync(
        TikaiDbContext db, TimeProvider timeProvider, IOptions<OrchestratorOptions> options, CancellationToken cancellationToken)
    {
        var since = timeProvider.GetUtcNow() - _recentlyEnded;
        var runs = await QueryAsync(db, options, db.AgentRuns.Where(r => !AgentRunStates.Ended.Contains(r.State) || r.EndedAt >= since), cancellationToken);
        return TypedResults.Ok(runs.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).ToList());
    }

    /// <summary>The ticket's runs, newest first.</summary>
    private static async Task<Results<Ok<List<RunInfo>>, ProblemHttpResult>> ListAsync(
        string slug, int number, TikaiDbContext db, IOptions<OrchestratorOptions> options, CancellationToken cancellationToken)
    {
        var project = await ProjectEndpoints.FindAsync(db, slug, cancellationToken);
        if (project is null)
        {
            return ProjectEndpoints.NotFound();
        }
        var ticketId = await db.Tickets.Where(t => t.ProjectId == project.Id && t.Number == number).Select(t => (Guid?)t.Id).SingleOrDefaultAsync(cancellationToken);
        if (ticketId is null)
        {
            return ProjectEndpoints.NotFound("Ticket");
        }
        var runs = await QueryAsync(db, options, db.AgentRuns.Where(r => r.TicketId == ticketId), cancellationToken);
        return TypedResults.Ok(runs.OrderByDescending(r => r.CreatedAt).ToList());
    }

    private static async Task<Results<Ok<RunInfo>, ProblemHttpResult>> GetAsync(
        Guid id, TikaiDbContext db, IOptions<OrchestratorOptions> options, CancellationToken cancellationToken)
    {
        var run = (await QueryAsync(db, options, db.AgentRuns.Where(r => r.Id == id), cancellationToken)).SingleOrDefault();
        return run is null ? NotFound() : TypedResults.Ok(run);
    }

    /// <summary>The run's transcript after <paramref name="afterSeq"/>, oldest first.</summary>
    private static async Task<Results<Ok<List<RunEventInfo>>, ProblemHttpResult>> EventsAsync(
        Guid id, TikaiDbContext db, CancellationToken cancellationToken, long afterSeq = 0, int limit = 200)
    {
        if (!await db.AgentRuns.AnyAsync(r => r.Id == id, cancellationToken))
        {
            return NotFound();
        }
        var rows = await db.RunEvents.AsNoTracking()
            .Where(e => e.RunId == id && e.Seq > afterSeq)
            .OrderBy(e => e.Seq)
            .Take(Math.Clamp(limit, 1, MaxEventsPerPage))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(rows.Select(ToInfo).ToList());
    }

    internal static RunEventInfo ToInfo(RunEvent e) => new(e.Seq, e.Kind, JsonDocument.Parse(e.Payload).RootElement.Clone(), e.CreatedAt);

    /// <summary>Stops the run's agent. The run is cancelled once the container has ended (at once if it has none).</summary>
    private static async Task<Results<Accepted<RunInfo>, ProblemHttpResult>> CancelAsync(
        Guid id, TikaiDbContext db, RunControl control, IOptions<OrchestratorOptions> options, CancellationToken cancellationToken)
    {
        if (!await db.AgentRuns.AnyAsync(r => r.Id == id, cancellationToken))
        {
            return NotFound();
        }
        if (!await control.CancelAsync(id, cancellationToken))
        {
            return Conflict("Run has ended", "This run has already ended.");
        }
        var info = (await QueryAsync(db, options, db.AgentRuns.Where(r => r.Id == id), cancellationToken)).Single();
        return TypedResults.Accepted($"/api/runs/{id}", info);
    }

    /// <summary>The runs with what they wait for. Filter <paramref name="runs"/> before calling: EF cannot filter after the projection.</summary>
    internal static async Task<List<RunInfo>> QueryAsync(
        TikaiDbContext db, IOptions<OrchestratorOptions> options, IQueryable<AgentRun> runs, CancellationToken cancellationToken)
    {
        var rows = await Query(db, runs).ToListAsync(cancellationToken);
        var ids = rows.Select(r => r.Id).ToList();
        var open = await db.AttentionItems.AsNoTracking()
            .Where(i => i.RunId != null && ids.Contains(i.RunId.Value) && i.Status == AttentionStatus.Open && AttentionKinds.Blocking.Contains(i.Kind))
            .OrderBy(i => i.CreatedAt).ThenBy(i => i.Id)
            .ToListAsync(cancellationToken);
        var byRun = open.ToLookup(i => i.RunId!.Value);
        return [.. rows.Select(r =>
        {
            var waiting = byRun[r.Id].Select(i => new WaitingItem(
                i.Id,
                i.Kind,
                i.Kind == AttentionKind.Question ? AttentionJson.Deserialize<QuestionPayload>(i.Payload) : null,
                i.Kind == AttentionKind.PlanApproval ? AttentionJson.Deserialize<PlanPayload>(i.Payload) : null,
                i.CreatedAt)).ToList();
            DateTimeOffset? since = waiting.Count > 0 ? waiting[0].CreatedAt : null;
            var blocked = r.Info.State == AgentRunState.Blocked;
            return r.Info with
            {
                Waiting = waiting,
                BlockedSince = since,
                BlockTimeoutAt = blocked && r.HasContainer ? since + options.Value.BlockTimeout : null,
                AgentStopped = blocked && !r.HasContainer,
            };
        })];
    }

    private sealed record RunRow(Guid Id, bool HasContainer, RunInfo Info);

    private static IQueryable<RunRow> Query(TikaiDbContext db, IQueryable<AgentRun> runs) =>
        from r in runs
        join t in db.Tickets on r.TicketId equals t.Id
        join p in db.Projects on t.ProjectId equals p.Id
        join repo in db.Repositories on r.RepositoryId equals repo.Id into repos
        from repo in repos.DefaultIfEmpty()
        select new RunRow(r.Id, r.ContainerId != null, new RunInfo(
            r.Id,
            p.Key + "-" + t.Number,
            t.Number,
            p.Slug,
            t.Title,
            r.State,
            r.Adapter,
            r.Model,
            r.RepositoryId,
            repo == null ? null : repo.Name,
            r.LlmProviderId,
            r.Branch,
            r.ProgressStep,
            r.ProgressNote,
            r.ProgressAt,
            r.ExitCode,
            r.FailureReason,
            r.CostUsd,
            r.CostTokens,
            r.CancelRequestedAt != null,
            r.CreatedAt,
            r.StartedAt,
            r.EndedAt,
            r.Attempt,
            r.AttemptKind,
            Array.Empty<WaitingItem>(),
            null,
            null,
            false));

    private static ProblemHttpResult NotFound() => ProjectEndpoints.NotFound("Run");

    private static ProblemHttpResult Conflict(string title, string detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: title, detail: detail);
}
