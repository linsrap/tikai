using Microsoft.EntityFrameworkCore;
using Tikai.Core.Projects;
using Tikai.Core.Tickets;
using Tikai.Data;
using Tikai.Data.Tickets;

namespace Tikai.Api.Tickets;

/// <summary>Creates tickets with their number and applies edits to a ticket and its criteria as one change.</summary>
internal sealed class TicketService(TikaiDbContext db, TimeProvider timeProvider)
{
    public Task<Ticket?> FindAsync(Guid projectId, int number, CancellationToken cancellationToken) =>
        db.Tickets.Include(t => t.Criteria).SingleOrDefaultAsync(t => t.ProjectId == projectId && t.Number == number, cancellationToken);

    public async Task<Ticket> CreateAsync(Project project, CreateTicketRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var number = await db.NextAsync(project.Id, cancellationToken)
            ?? throw new InvalidOperationException($"Project {project.Id} disappeared while a ticket was created.");
        var ticket = new Ticket
        {
            Id = NewId(),
            ProjectId = project.Id,
            Number = number,
            Title = request.Title.Trim(),
            Description = request.Description,
            Status = TicketStatus.Open,
            Priority = request.Priority,
        };
        foreach (var (text, position) in (request.Criteria ?? []).Select((text, i) => (text, i)))
        {
            ticket.Criteria.Add(new AcceptanceCriterion { Id = NewId(), Text = text.Trim(), Position = position });
        }
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ticket;
    }

    /// <summary>
    /// Applies the request to a ticket loaded with <see cref="FindAsync"/>. Throws
    /// <see cref="DbUpdateConcurrencyException"/> when someone else saved the ticket in between.
    /// </summary>
    /// <returns>Null when saved, otherwise a message for people (a criterion id that is not this ticket's).</returns>
    public async Task<string?> UpdateAsync(Ticket ticket, UpdateTicketRequest request, CancellationToken cancellationToken)
    {
        var existing = ticket.Criteria.ToDictionary(c => c.Id);
        var kept = new HashSet<Guid>();
        foreach (var (input, position) in request.Criteria.Select((input, i) => (input, i)))
        {
            if (input.Id is not { } id)
            {
                ticket.Criteria.Add(new AcceptanceCriterion { Id = NewId(), Text = input.Text.Trim(), Position = position });
            }
            else if (existing.TryGetValue(id, out var criterion) && kept.Add(id))
            {
                criterion.Text = input.Text.Trim();
                criterion.Position = position;
            }
            else
            {
                return $"Criterion {position + 1} is not one of this ticket's criteria (or is listed twice). Reload the ticket and try again.";
            }
        }
        foreach (var removed in existing.Values.Where(c => !kept.Contains(c.Id)))
        {
            ticket.Criteria.Remove(removed);
            db.AcceptanceCriteria.Remove(removed);
        }

        ticket.Title = request.Title.Trim();
        ticket.Description = request.Description;
        ticket.Status = request.Status;
        ticket.Priority = request.Priority;
        // Always write the ticket row, also when only criteria changed: its xmin then guards the whole ticket.
        db.Entry(ticket).Property(t => t.UpdatedAt).IsModified = true;
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    // Set here, not by the save interceptor: several new criteria with an empty id would collide in the change tracker.
    private Guid NewId() => Guid.CreateVersion7(timeProvider.GetUtcNow());
}
