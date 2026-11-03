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

    /// <summary>
    /// The open review threads of the run's change set that have comments sent to the agent: those comments and the
    /// agent's own replies (notes among humans are left out).
    /// </summary>
    Task<IReadOnlyList<ReviewThreadResult>> GetReviewCommentsAsync(RunScope run, CancellationToken cancellationToken);

    /// <summary>Adds the agent's reply to a thread it can see; null when there is no such thread.</summary>
    Task<string?> ReplyToThreadAsync(RunScope run, Guid threadId, string body, CancellationToken cancellationToken);
}

/// <summary>A review thread as get_review_comments returns it.</summary>
/// <param name="ThreadId">For reply_to_thread.</param>
/// <param name="Round">The review round the thread is about; its lines are lines of that round's version.</param>
/// <param name="Path">Null for a general comment on the whole change.</param>
/// <param name="Side">"new" for lines of your version, "old" for lines of the version before your change.</param>
public sealed record ReviewThreadResult(
    string ThreadId,
    int Round,
    string? Path,
    string? Side,
    int? LineStart,
    int? LineEnd,
    IReadOnlyList<ReviewCommentResult> Comments);

/// <param name="Author">"reviewer" or "you".</param>
public sealed record ReviewCommentResult(string Author, string Body, DateTimeOffset At);

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
