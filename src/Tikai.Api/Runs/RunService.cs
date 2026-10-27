using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Tikai.Api.Attention;
using Tikai.Api.Llm;
using Tikai.Core.Events;
using Tikai.Core.Jobs;
using Tikai.Core.Projects;
using Tikai.Core.Runs;
using Tikai.Core.Tickets;
using Tikai.Data;
using Tikai.Orchestrator;

namespace Tikai.Api.Runs;

/// <summary>What a new agent run works with.</summary>
/// <param name="TicketKey">For the branch name, e.g. "HUB-42".</param>
internal sealed record NewRun(Guid TicketId, string TicketKey, Guid RepositoryId, Guid LlmProviderId, string Model, Guid StartedById);

/// <summary>Why a run could not be started: an HTTP status with a title and detail, or a field that is wrong.</summary>
internal sealed record StartProblem(int Status, string Title, string Detail, string? Field = null);

/// <summary>
/// Creates runs and hands out their MCP tokens. A token is returned once and only its hash is kept; the start job
/// renews it for the run's first container (and every later one), so it only ever exists in that container's config.
/// </summary>
internal sealed class RunService(
    TikaiDbContext db,
    LlmProviderService providers,
    AttentionService attention,
    IJobQueue jobs,
    IEventPublisher events,
    TimeProvider timeProvider)
{
    /// <summary>
    /// Assigns the ticket to the agent: a queued run on its own branch and a start job, in one transaction. The ticket
    /// moves to in_progress, and its open failure items are dismissed (the new run answers them). One active run per
    /// ticket and per repository.
    /// </summary>
    /// <param name="repositoryId">Needed when the project has several repositories.</param>
    /// <param name="providerId">By default the project's provider, else the person's own.</param>
    public async Task<(AgentRun? Run, StartProblem? Problem)> StartAsync(
        Project project, Ticket ticket, Guid? repositoryId, Guid? providerId, Guid userId, CancellationToken cancellationToken)
    {
        if (ticket.Status is not (TicketStatus.Open or TicketStatus.InProgress))
        {
            return Problem(Conflict("Ticket is not open", "Only an open or in-progress ticket can be assigned to an agent. Reopen it first."));
        }

        var repositories = await db.Repositories.AsNoTracking().Where(r => r.ProjectId == project.Id).OrderBy(r => r.Name).ToListAsync(cancellationToken);
        var repository = repositoryId is { } wanted
            ? repositories.SingleOrDefault(r => r.Id == wanted)
            : repositories.Count == 1 ? repositories[0] : null;
        if (repositories.Count == 0)
        {
            return Problem(Conflict("No repository", "Add a repository to this project first; the agent works on one of them."));
        }
        if (repository is null)
        {
            return Problem(new(StatusCodes.Status400BadRequest, "Invalid repository", repositoryId is null
                ? "This project has several repositories: choose the one the agent works on."
                : "Choose one of this project's repositories.", "repositoryId"));
        }
        if (repository.Status != RepositoryStatus.Ready)
        {
            return Problem(Conflict("Repository not ready", $"\"{repository.Name}\" is {SnakeCase(repository.Status)}; wait for the clone to finish or fix it first."));
        }

        var usable = providers.UsableFor(userId, project.Id);
        var provider = providerId is { } chosen
            ? await usable.SingleOrDefaultAsync(p => p.Id == chosen, cancellationToken)
            : await usable.FirstOrDefaultAsync(cancellationToken);
        if (provider is null && providerId is not null)
        {
            return Problem(new(StatusCodes.Status400BadRequest, "Invalid provider", "Choose one of this project's providers or one of your own.", "providerId"));
        }
        if (provider is null)
        {
            return Problem(Conflict("No LLM provider", "Add an LLM provider to this project (on the project page) or your own under Settings. The agent uses its API key."));
        }

        if (await db.AgentRuns.AnyAsync(r => (r.TicketId == ticket.Id || r.RepositoryId == repository.Id) && !AgentRunStates.Ended.Contains(r.State), cancellationToken))
        {
            return Problem(_activeRunConflict);
        }

        AgentRun run;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            run = await CreateAsync(new NewRun(ticket.Id, TicketKey.Format(project.Key, ticket.Number), repository.Id, provider.Id, provider.DefaultModel, userId),
                cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Problem(_activeRunConflict);
        }
        var now = timeProvider.GetUtcNow();
        await db.Tickets.Where(t => t.Id == ticket.Id && t.Status == TicketStatus.Open)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TicketStatus.InProgress).SetProperty(t => t.UpdatedAt, now), cancellationToken);
        await attention.DismissFailuresAsync(ticket.Id, cancellationToken);
        await jobs.EnqueueAsync(RunJob.Queue, new RunJob(run.Id).ToJson(), maxAttempts: 3, cancellationToken: cancellationToken);
        await events.PublishAsync(AgentRun.Topic, run.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (run, null);
    }

    private static readonly StartProblem _activeRunConflict =
        Conflict("A run is active", "This ticket or this repository already has an active run. Wait for it to end or cancel it.");

    private static StartProblem Conflict(string title, string detail) => new(StatusCodes.Status409Conflict, title, detail);

    private static (AgentRun?, StartProblem?) Problem(StartProblem problem) => (null, problem);

    private static string SnakeCase<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    /// <summary>A queued run for the ticket, and its token for the run's MCP config.</summary>
    public Task<(AgentRun Run, string Token)> CreateAsync(Guid ticketId, CancellationToken cancellationToken) =>
        AddAsync(new AgentRun { TicketId = ticketId, State = AgentRunState.Queued, TokenHash = [] }, cancellationToken);

    /// <summary>A queued agent run on its own branch. Joins the caller's transaction.</summary>
    public async Task<AgentRun> CreateAsync(NewRun request, CancellationToken cancellationToken)
    {
        var id = Guid.CreateVersion7(timeProvider.GetUtcNow());
        var (run, _) = await AddAsync(new AgentRun
        {
            Id = id,
            TicketId = request.TicketId,
            RepositoryId = request.RepositoryId,
            LlmProviderId = request.LlmProviderId,
            StartedById = request.StartedById,
            State = AgentRunState.Queued,
            TokenHash = [],
            Adapter = RunRules.ClaudeCodeAdapter,
            Model = request.Model,
            Branch = RunBranch.Name(request.TicketKey, id),
        }, cancellationToken);
        return run;
    }

    /// <summary>
    /// Replaces the run's token, e.g. when a new container takes over the run: the old container's token stops working.
    /// </summary>
    /// <returns>The new token, or null when the run does not exist or has ended.</returns>
    public async Task<string?> RenewTokenAsync(Guid runId, CancellationToken cancellationToken)
    {
        var token = RunToken.New();
        var hash = RunToken.Hash(token);
        var now = timeProvider.GetUtcNow();
        var updated = await db.AgentRuns
            .Where(r => r.Id == runId && !AgentRunStates.Ended.Contains(r.State))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.TokenHash, hash).SetProperty(r => r.UpdatedAt, now), cancellationToken);
        return updated == 0 ? null : token;
    }

    private async Task<(AgentRun Run, string Token)> AddAsync(AgentRun run, CancellationToken cancellationToken)
    {
        var token = RunToken.New();
        if (run.Id == Guid.Empty)
        {
            run.Id = Guid.CreateVersion7(timeProvider.GetUtcNow());
        }
        run.TokenHash = RunToken.Hash(token);
        db.AgentRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        return (run, token);
    }
}
