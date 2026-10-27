using Tikai.Core.Attention;

namespace Tikai.Mcp;

/// <summary>What the MCP tools need from the hub, always for one run. Implemented by the API host.</summary>
public interface IRunTools
{
    Task<TicketResult> GetTicketAsync(RunScope run, CancellationToken cancellationToken);

    Task ReportProgressAsync(RunScope run, string currentStep, string? note, CancellationToken cancellationToken);

    /// <summary>
    /// Takes the run branch into the hub's mirror and records a review round with the agent's self-report; the run
    /// moves to in_review. Not accepted when there is nothing to submit or the self-report is wrong.
    /// </summary>
    Task<SubmitResult> SubmitForReviewAsync(
        RunScope run, string? summary, IReadOnlyList<CriterionMapping>? criteriaMapping, IReadOnlyList<string?>? openConcerns, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a blocking item, or reuses the run's latest one when it is the same request (a dropped call asked again).
    /// Returns the stored answer at once when that item is already answered.
    /// </summary>
    Task<RaisedItem> RaiseAsync<TPayload>(RunScope run, AttentionKind kind, TPayload payload, CancellationToken cancellationToken);

    /// <summary>The item's answer is being returned to the agent; once it has every answer, its run runs again.</summary>
    Task DeliveredAsync(RunScope run, Guid itemId, CancellationToken cancellationToken);
}

/// <param name="Message">For the agent: what was submitted, or what to fix.</param>
public sealed record SubmitResult(bool Accepted, string Message);

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
