using Microsoft.EntityFrameworkCore;
using Tikai.Core.Runs;
using Tikai.Data;

namespace Tikai.Api.Runs;

/// <summary>
/// Creates runs and hands out their MCP tokens. The orchestrator (roadmap step 10) calls it; until then
/// only tests do. A token is returned once and only its hash is kept.
/// </summary>
internal sealed class RunService(TikaiDbContext db, TimeProvider timeProvider)
{
    /// <summary>A queued run for the ticket, and its token for the run's MCP config.</summary>
    public async Task<(AgentRun Run, string Token)> CreateAsync(Guid ticketId, CancellationToken cancellationToken)
    {
        var token = RunToken.New();
        var run = new AgentRun
        {
            Id = Guid.CreateVersion7(timeProvider.GetUtcNow()),
            TicketId = ticketId,
            State = AgentRunState.Queued,
            TokenHash = RunToken.Hash(token),
        };
        db.AgentRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        return (run, token);
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
}
