using Tikai.Core.Attention;

namespace Tikai.Mcp;

/// <summary>What the MCP tools need from the hub, always for one run. Implemented by the API host.</summary>
public interface IRunTools
{
    Task<TicketResult> GetTicketAsync(RunScope run, CancellationToken cancellationToken);

    Task ReportProgressAsync(RunScope run, string currentStep, string? note, CancellationToken cancellationToken);

    /// <summary>Records the agent's self-report as a review item.</summary>
    /// <returns>Null when recorded, otherwise a message for the agent (e.g. a criterion id that is not the ticket's).</returns>
    Task<string?> SubmitForReviewAsync(
        RunScope run, string? summary, IReadOnlyList<CriterionMapping>? criteriaMapping, IReadOnlyList<string?>? openConcerns, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a blocking item, or reuses the run's latest one when it is the same request (a dropped call asked again).
    /// Returns the stored answer at once when that item is already answered.
    /// </summary>
    Task<RaisedItem> RaiseAsync<TPayload>(RunScope run, AttentionKind kind, TPayload payload, CancellationToken cancellationToken);

    /// <summary>The item's answer is being returned to the agent; once it has every answer, its run runs again.</summary>
    Task DeliveredAsync(RunScope run, Guid itemId, CancellationToken cancellationToken);
}

/// <param name="Merged">True when an earlier identical request's item was reused.</param>
/// <param name="Answer">Set when that item is already answered.</param>
public sealed record RaisedItem(Guid ItemId, bool Merged, AttentionAnswer? Answer);

/// <summary>The ticket as get_ticket returns it.</summary>
/// <param name="Description">Markdown, exactly as written.</param>
/// <param name="Criteria">In order; refer to them by id in submit_for_review.</param>
public sealed record TicketResult(
    string Key,
    string Title,
    string Description,
    string Status,
    string Priority,
    IReadOnlyList<CriterionResult> Criteria);

public sealed record CriterionResult(Guid Id, string Text);
