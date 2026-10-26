using Tikai.Core.Common;

namespace Tikai.Core.Tickets;

/// <summary>
/// A unit of work in a project: what is wanted (title, markdown description) and how to tell it is done
/// (acceptance criteria). <see cref="Number"/> is per project and never reused, see <see cref="TicketKey"/>.
/// </summary>
public class Ticket : Entity
{
    public Guid ProjectId { get; set; }

    /// <summary>Assigned on creation from the project's counter; 1, 2, 3… without gaps.</summary>
    public int Number { get; set; }

    public required string Title { get; set; }

    /// <summary>Markdown.</summary>
    public string Description { get; set; } = "";

    public TicketStatus Status { get; set; }

    public TicketPriority Priority { get; set; } = TicketPriority.Normal;

    /// <summary>In order of <see cref="AcceptanceCriterion.Position"/> once loaded with it.</summary>
    public List<AcceptanceCriterion> Criteria { get; } = [];
}

/// <summary>One checkable condition of a ticket. Its id stays the same when the text or order changes.</summary>
public class AcceptanceCriterion : Entity
{
    public Guid TicketId { get; set; }

    /// <summary>0-based place in the ticket's list.</summary>
    public int Position { get; set; }

    public required string Text { get; set; }
}

public enum TicketStatus
{
    Open,
    InProgress,
    InReview,
    Done,
    Closed,
}

public enum TicketPriority
{
    Low,
    Normal,
    High,
    Urgent,
}
