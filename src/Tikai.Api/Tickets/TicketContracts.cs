using Tikai.Core.Tickets;
using Tikai.Llm.Refine;

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

/// <summary>A ticket as it is on screen (maybe not saved yet), to be refined by an LLM.</summary>
/// <param name="ProviderId">Null: the project's provider, otherwise the user's own.</param>
/// <param name="MinSize">"More detail": the proposal is at least this size.</param>
public sealed record RefineSpecRequest(
    string Title,
    string Description = "",
    IReadOnlyList<string>? Criteria = null,
    Guid? ProviderId = null,
    SpecSize? MinSize = null);

/// <summary>
/// The proposal, only where it differs from the draft. Nothing is stored: the person takes over what they want and
/// saves the ticket. A null title or description means there is no change to that field.
/// </summary>
/// <param name="Criteria">New criteria and rewrites; a rewrite's <c>Replaces</c> is the 0-based place in the request's criteria.</param>
/// <param name="Assumptions">What the model assumed; accepted unless the person unticks it.</param>
/// <param name="PromptVersion">The prompt file that produced it, e.g. "refine-spec.v2".</param>
public sealed record SpecProposalInfo(
    SpecSize Size,
    string? Title,
    string? Description,
    IReadOnlyList<CriterionChange> Criteria,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<SpecQuestion> Questions,
    string PromptVersion,
    Guid ProviderId,
    string ProviderName,
    string Model,
    int DurationMs);
