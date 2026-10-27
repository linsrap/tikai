using Microsoft.EntityFrameworkCore;
using Tikai.Core.Runs;
using Tikai.Data;

namespace Tikai.Api.Runs;

/// <summary>What a new agent run works with.</summary>
/// <param name="TicketKey">For the branch name, e.g. "HUB-42".</param>
internal sealed record NewRun(Guid TicketId, string TicketKey, Guid RepositoryId, Guid LlmProviderId, string Model, Guid StartedById);

/// <summary>
/// Creates runs and hands out their MCP tokens. A token is returned once and only its hash is kept; the start job
/// renews it for the run's first container (and every later one), so it only ever exists in that container's config.
/// </summary>
internal sealed class RunService(TikaiDbContext db, TimeProvider timeProvider)
{
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
