using Tikai.Core.Tickets;

namespace Tikai.Api.Tickets;

/// <summary>A ticket in a list: no description, only how many criteria it has.</summary>
public sealed record TicketSummary(
    Guid Id,
    int Number,
    string Key,
    string Title,
    TicketStatus Status,
    TicketPriority Priority,
    int CriteriaCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <param name="Key">The project key and number, e.g. "HUB-42".</param>
/// <param name="Description">Markdown.</param>
public sealed record TicketInfo(
    Guid Id,
    int Number,
    string Key,
    string Title,
    string Description,
    TicketStatus Status,
    TicketPriority Priority,
    IReadOnlyList<CriterionInfo> Criteria,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    internal static TicketInfo From(Ticket t, string projectKey) => new(
        t.Id,
        t.Number,
        TicketKey.Format(projectKey, t.Number),
        t.Title,
        t.Description,
        t.Status,
        t.Priority,
        [.. t.Criteria.OrderBy(c => c.Position).Select(c => new CriterionInfo(c.Id, c.Text))],
        t.CreatedAt,
        t.UpdatedAt);
}

/// <summary>A criterion; its place in the list is its position.</summary>
public sealed record CriterionInfo(Guid Id, string Text);

/// <param name="Criteria">Criterion texts, in order.</param>
public sealed record CreateTicketRequest(
    string Title,
    string Description = "",
    TicketPriority Priority = TicketPriority.Normal,
    IReadOnlyList<string>? Criteria = null);

/// <param name="Criteria">
/// The complete new list, in order. An entry with an id keeps that criterion, one without is added,
/// and criteria that are not listed are deleted.
/// </param>
public sealed record UpdateTicketRequest(
    string Title,
    string Description,
    TicketStatus Status,
    TicketPriority Priority,
    IReadOnlyList<CriterionInput> Criteria);

/// <param name="Id">Null for a new criterion.</param>
public sealed record CriterionInput(string Text, Guid? Id = null);
