using Microsoft.EntityFrameworkCore;
using Tikai.Core.Agents;
using Tikai.Core.Runs;

namespace Tikai.Data.Runs;

/// <summary>
/// Appends run events with numbers from a counter on the run row (<c>agent_runs.last_event_seq</c>). The orchestrator
/// and the MCP tools both write events of one run, so the numbers come from the row lock, not from MAX(seq)+1.
/// </summary>
public static class RunEventLog
{
    /// <summary>
    /// Inserts the events in order and, when given, moves the run's log cursor in the same statement. Call it inside a
    /// transaction, so numbers and cursor commit with the events or not at all.
    /// </summary>
    public static async Task AppendAsync(
        this TikaiDbContext db, Guid runId, IReadOnlyList<ParsedEvent> events, long? logLinesRead, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (events.Count == 0 && logLinesRead is null)
        {
            return;
        }
        var count = (long)events.Count;
        // Not composed further: EF would wrap the UPDATE in a subquery, which Postgres does not allow.
        var last = await db.Database.SqlQuery<long>($"""
            UPDATE agent_runs
            SET last_event_seq = last_event_seq + {count}, log_lines_read = COALESCE({logLinesRead}, log_lines_read), updated_at = {now}
            WHERE id = {runId}
            RETURNING last_event_seq AS "Value"
            """).ToListAsync(cancellationToken);
        if (last.Count == 0)
        {
            throw new InvalidOperationException($"Run {runId} does not exist.");
        }
        var seq = last[0] - count;
        foreach (var e in events)
        {
            db.RunEvents.Add(new RunEvent
            {
                Id = Guid.CreateVersion7(now),
                RunId = runId,
                Seq = ++seq,
                Kind = e.Kind,
                Payload = e.Payload.ToJsonString(),
            });
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}
