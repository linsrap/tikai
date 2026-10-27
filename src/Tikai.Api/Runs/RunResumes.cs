using Microsoft.EntityFrameworkCore;
using Tikai.Core.Attention;
using Tikai.Core.Jobs;
using Tikai.Core.Runs;
using Tikai.Data;
using Tikai.Orchestrator;

namespace Tikai.Api.Runs;

/// <summary>
/// When a blocked run gets a new container: once it has none (its agent stopped waiting, or was stopped at the block
/// timeout) and nothing of it is left unanswered. Called when an item is answered, when a container ends and leaves
/// the run blocked, and by the reconciler; each of them may be first.
/// </summary>
internal static class RunResumes
{
    /// <summary>Enqueues a resume job if the run is ready for one. Must run inside the caller's transaction.</summary>
    /// <returns>True when a job was enqueued.</returns>
    public static async Task<bool> RequestIfReadyAsync(TikaiDbContext db, IJobQueue jobs, Guid runId, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("A resume is requested inside a transaction.");
        }
        // Answer, container end and reconciler queue on the run row, so only one of them enqueues.
        var run = await LockAsync(db, runId, cancellationToken);
        if (run is not { State: AgentRunState.Blocked, ContainerId: null, CancelRequestedAt: null })
        {
            return false;
        }
        var open = await db.AttentionItems.AnyAsync(
            i => i.RunId == runId && i.Status == AttentionStatus.Open && AttentionKinds.Blocking.Contains(i.Kind), cancellationToken);
        if (open || await HasLiveJobAsync(db, runId, cancellationToken))
        {
            return false;
        }
        await jobs.EnqueueAsync(RunJob.Queue, new RunJob(runId, RunJobKind.Resume).ToJson(), maxAttempts: 3, cancellationToken: cancellationToken);
        return true;
    }

    /// <summary>The run row, locked until the transaction ends.</summary>
    public static async Task<AgentRun?> LockAsync(TikaiDbContext db, Guid runId, CancellationToken cancellationToken)
    {
        // Not FromSql("SELECT * … FOR UPDATE"): the entity maps xmin, which SELECT * does not return.
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM agent_runs WHERE id = {runId} FOR UPDATE", cancellationToken);
        return await db.AgentRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, cancellationToken);
    }

    /// <summary>A runs job of the run is waiting or being worked on (a dead one is not live).</summary>
    public static async Task<bool> HasLiveJobAsync(TikaiDbContext db, Guid runId, CancellationToken cancellationToken) =>
        await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (
                SELECT 1 FROM jobs
                WHERE queue = {RunJob.Queue} AND dead_at IS NULL AND payload->>'run_id' = {runId.ToString("D")}) AS "Value"
            """).SingleAsync(cancellationToken);
}
