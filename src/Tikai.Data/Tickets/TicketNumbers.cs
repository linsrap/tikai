using Microsoft.EntityFrameworkCore;

namespace Tikai.Data.Tickets;

/// <summary>
/// Hands out ticket numbers from a counter on the project row (<c>projects.last_ticket_number</c>).
/// Not MAX(number)+1, which gives two concurrent creators the same number.
/// </summary>
public static class TicketNumbers
{
    internal const string CounterProperty = "LastTicketNumber";

    /// <summary>
    /// Takes the project's next number. Call it inside the transaction that inserts the ticket: the row lock
    /// queues other creators until that transaction ends, and a rollback gives the number back, so there are no gaps.
    /// </summary>
    /// <returns>The number, or null when the project does not exist.</returns>
    public static async Task<int?> NextAsync(this TikaiDbContext db, Guid projectId, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Ticket numbers must be taken inside the transaction that inserts the ticket.");
        }
        // Not composed further: EF would wrap the UPDATE in a subquery, which Postgres does not allow.
        var numbers = await db.Database
            .SqlQuery<int>($"UPDATE projects SET last_ticket_number = last_ticket_number + 1 WHERE id = {projectId} RETURNING last_ticket_number AS \"Value\"")
            .ToListAsync(cancellationToken);
        return numbers.Count == 0 ? null : numbers[0];
    }
}
