using Microsoft.EntityFrameworkCore;
using Tikai.Api.Runs;
using Tikai.Core.Attention;
using Tikai.Core.Events;
using Tikai.Core.Jobs;
using Tikai.Core.Runs;
using Tikai.Data;
using Tikai.Mcp;

namespace Tikai.Api.Attention;

/// <summary>
/// Writes attention items: raised by agents (blocking or not) and answered by people. Every change publishes an event.
/// A run is blocked while one of its blocking items is open, and runs again once the agent has every answer: the
/// state changes in the same transactions as the items.
/// </summary>
internal sealed class AttentionService(TikaiDbContext db, IJobQueue jobs, IEventPublisher events, TimeProvider timeProvider)
{
    /// <summary>
    /// Creates a blocking item, or reuses the run's latest blocking item when it is the same request: a call that
    /// dropped (hub restart, client timeout) and is asked again must not become a second inbox item, and must get
    /// an answer that was given meanwhile. While the item is open the run is blocked.
    /// </summary>
    public async Task<RaisedItem> RaiseAsync<TPayload>(RunScope run, AttentionKind kind, TPayload payload, CancellationToken cancellationToken)
    {
        var hash = AttentionJson.RequestHash(kind, payload);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Identical calls arriving together queue on the run row, so only one of them inserts.
        await RunResumes.LockAsync(db, run.RunId, cancellationToken);

        // Only the latest blocking item: the same question asked again much later, after others, is a new question.
        var latest = await db.AttentionItems.AsNoTracking()
            .Where(i => i.RunId == run.RunId && AttentionKinds.Blocking.Contains(i.Kind))
            .OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (latest is not null && latest.Kind == kind && latest.RequestHash == hash && latest.Status != AttentionStatus.Dismissed)
        {
            var stored = AnswerOf(latest);
            if (stored is null)
            {
                await BlockAsync(run.RunId, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return new RaisedItem(latest.Id, Merged: true, stored);
        }

        var item = await AddAsync(run, kind, payload, hash, cancellationToken);
        await BlockAsync(run.RunId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RaisedItem(item.Id, Merged: false, Answer: null);
    }

    /// <summary>
    /// The answer reached the agent (as a tool result). Once the agent has every answer, the run runs again; only a run
    /// whose container is there, because only a live container calls tools.
    /// </summary>
    public async Task DeliveredAsync(RunScope run, Guid itemId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await RunResumes.LockAsync(db, run.RunId, cancellationToken);
        await db.AttentionItems.Where(i => i.Id == itemId && i.RunId == run.RunId && i.Status == AttentionStatus.Answered && i.DeliveredAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.DeliveredAt, now).SetProperty(i => i.UpdatedAt, now), cancellationToken);
        var stillOpen = await db.AttentionItems.AnyAsync(
            i => i.RunId == run.RunId && i.Status == AttentionStatus.Open && AttentionKinds.Blocking.Contains(i.Kind), cancellationToken);
        if (!stillOpen)
        {
            var unblocked = await db.AgentRuns.Where(r => r.Id == run.RunId && r.State == AgentRunState.Blocked && r.ContainerId != null)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.State, AgentRunState.Running).SetProperty(r => r.UpdatedAt, now), cancellationToken);
            if (unblocked > 0)
            {
                await events.PublishAsync(AgentRun.Topic, run.RunId, cancellationToken);
            }
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Records an item that does not block the agent (e.g. a submitted review).</summary>
    /// <remarks>Joins the caller's transaction, if any.</remarks>
    public async Task<AttentionItem> RecordAsync<TPayload>(RunScope run, AttentionKind kind, TPayload payload, CancellationToken cancellationToken)
    {
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var item = await AddAsync(run, kind, payload, requestHash: null, cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        return item;
    }

    /// <summary>
    /// Stores the answer if the item is still open. False when someone answered it first (or it was dismissed). A waiting
    /// call gets it through the waiter; a run whose agent no longer waits gets a new container once nothing is open.
    /// </summary>
    public async Task<bool> AnswerAsync(Guid itemId, AttentionAnswer answer, Guid userId, CancellationToken cancellationToken)
    {
        var json = AttentionJson.Serialize(answer);
        var now = timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var updated = await db.AttentionItems
            .Where(i => i.Id == itemId && i.Status == AttentionStatus.Open)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, AttentionStatus.Answered)
                .SetProperty(i => i.Answer, json)
                .SetProperty(i => i.AnsweredById, userId)
                .SetProperty(i => i.AnsweredAt, now)
                .SetProperty(i => i.UpdatedAt, now), cancellationToken);
        if (updated == 0)
        {
            return false;
        }
        await events.PublishAsync(AttentionItem.Topic, itemId, cancellationToken);
        if (await db.AttentionItems.Where(i => i.Id == itemId).Select(i => i.RunId).SingleAsync(cancellationToken) is { } runId)
        {
            await RunResumes.RequestIfReadyAsync(db, jobs, runId, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>The run has ended: its open questions and plans can no longer reach an agent. Joins the caller's transaction.</summary>
    public async Task DismissOpenBlockingAsync(Guid runId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var ids = await db.AttentionItems
            .Where(i => i.RunId == runId && i.Status == AttentionStatus.Open && AttentionKinds.Blocking.Contains(i.Kind))
            .Select(i => i.Id)
            .ToListAsync(cancellationToken);
        if (ids.Count == 0)
        {
            return;
        }
        await db.AttentionItems.Where(i => ids.Contains(i.Id) && i.Status == AttentionStatus.Open)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, AttentionStatus.Dismissed).SetProperty(i => i.UpdatedAt, now), cancellationToken);
        foreach (var id in ids)
        {
            await events.PublishAsync(AttentionItem.Topic, id, cancellationToken);
        }
    }

    /// <summary>
    /// A person closes an item no agent waits for (a failure, a submitted review). False when it is not open (any more).
    /// Joins the caller's transaction, if any.
    /// </summary>
    public async Task<bool> DismissAsync(Guid itemId, Guid userId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var updated = await db.AttentionItems
            .Where(i => i.Id == itemId && i.Status == AttentionStatus.Open && !AttentionKinds.Blocking.Contains(i.Kind))
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, AttentionStatus.Dismissed)
                .SetProperty(i => i.AnsweredById, userId)
                .SetProperty(i => i.UpdatedAt, now), cancellationToken);
        if (updated > 0)
        {
            await events.PublishAsync(AttentionItem.Topic, itemId, cancellationToken);
        }
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        return updated > 0;
    }

    /// <summary>A new run of the ticket answers its earlier failures. Joins the caller's transaction.</summary>
    public async Task DismissFailuresAsync(Guid ticketId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var ids = await db.AttentionItems
            .Where(i => i.TicketId == ticketId && i.Kind == AttentionKind.Failure && i.Status == AttentionStatus.Open)
            .Select(i => i.Id)
            .ToListAsync(cancellationToken);
        if (ids.Count == 0)
        {
            return;
        }
        await db.AttentionItems.Where(i => ids.Contains(i.Id) && i.Status == AttentionStatus.Open)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, AttentionStatus.Dismissed).SetProperty(i => i.UpdatedAt, now), cancellationToken);
        foreach (var id in ids)
        {
            await events.PublishAsync(AttentionItem.Topic, id, cancellationToken);
        }
    }

    public static AttentionAnswer? AnswerOf(AttentionItem item) =>
        item.Status == AttentionStatus.Answered && item.Answer is not null ? AttentionJson.Deserialize<AttentionAnswer>(item.Answer) : null;

    /// <summary>running → blocked while the run waits for a human (a second parallel item finds it blocked already).</summary>
    private async Task BlockAsync(Guid runId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var blocked = await db.AgentRuns.Where(r => r.Id == runId && r.State == AgentRunState.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.State, AgentRunState.Blocked).SetProperty(r => r.UpdatedAt, now), cancellationToken);
        if (blocked > 0)
        {
            await events.PublishAsync(AgentRun.Topic, runId, cancellationToken);
        }
    }

    private async Task<AttentionItem> AddAsync<TPayload>(RunScope run, AttentionKind kind, TPayload payload, string? requestHash, CancellationToken cancellationToken)
    {
        var item = new AttentionItem
        {
            Id = Guid.CreateVersion7(timeProvider.GetUtcNow()),
            Kind = kind,
            RunId = run.RunId,
            TicketId = run.TicketId,
            Status = AttentionStatus.Open,
            Payload = AttentionJson.Serialize(payload),
            RequestHash = requestHash,
        };
        db.AttentionItems.Add(item);
        await db.SaveChangesAsync(cancellationToken);
        await events.PublishAsync(AttentionItem.Topic, item.Id, cancellationToken);
        return item;
    }
}
