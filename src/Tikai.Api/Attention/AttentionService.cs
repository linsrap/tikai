using Microsoft.EntityFrameworkCore;
using Tikai.Core.Attention;
using Tikai.Core.Events;
using Tikai.Data;
using Tikai.Mcp;

namespace Tikai.Api.Attention;

/// <summary>Writes attention items: raised by agents (blocking or not) and answered by people. Every change publishes an event.</summary>
internal sealed class AttentionService(TikaiDbContext db, IEventPublisher events, TimeProvider timeProvider)
{
    /// <summary>
    /// Creates a blocking item, or reuses the run's latest blocking item when it is the same request: a call that
    /// dropped (hub restart, client timeout) and is asked again must not become a second inbox item, and must get
    /// an answer that was given meanwhile.
    /// </summary>
    public async Task<RaisedItem> RaiseAsync<TPayload>(RunScope run, AttentionKind kind, TPayload payload, CancellationToken cancellationToken)
    {
        var hash = AttentionJson.RequestHash(kind, payload);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Identical calls arriving together queue on the run row, so only one of them inserts.
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM agent_runs WHERE id = {run.RunId} FOR UPDATE", cancellationToken);

        // Only the latest blocking item: the same question asked again much later, after others, is a new question.
        var latest = await db.AttentionItems.AsNoTracking()
            .Where(i => i.RunId == run.RunId && AttentionKinds.Blocking.Contains(i.Kind))
            .OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (latest is not null && latest.Kind == kind && latest.RequestHash == hash && latest.Status != AttentionStatus.Dismissed)
        {
            await transaction.CommitAsync(cancellationToken);
            return new RaisedItem(latest.Id, Merged: true, AnswerOf(latest));
        }

        var item = await AddAsync(run, kind, payload, hash, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RaisedItem(item.Id, Merged: false, Answer: null);
    }

    /// <summary>Records an item that does not block the agent (e.g. a submitted review).</summary>
    public async Task<AttentionItem> RecordAsync<TPayload>(RunScope run, AttentionKind kind, TPayload payload, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var item = await AddAsync(run, kind, payload, requestHash: null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return item;
    }

    /// <summary>Stores the answer if the item is still open. False when someone answered it first.</summary>
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
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public static AttentionAnswer? AnswerOf(AttentionItem item) =>
        item.Status == AttentionStatus.Answered && item.Answer is not null ? AttentionJson.Deserialize<AttentionAnswer>(item.Answer) : null;

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
