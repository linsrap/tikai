using Tikai.Api.Runs;
using Tikai.Core.Attention;
using Tikai.Core.Runs;

namespace Tikai.Api.Attention;

/// <summary>
/// An inbox item with what a person needs to act on it without opening anything else. Exactly one of
/// <see cref="Question"/>, <see cref="Plan"/>, <see cref="Review"/> and <see cref="Failure"/> is set, matching
/// <see cref="Kind"/>.
/// </summary>
/// <param name="TicketKey">E.g. "HUB-42".</param>
/// <param name="Blocking">An agent waits for it (an open question or plan); these sort first in the inbox.</param>
/// <param name="Run">The run that raised it, as it is now.</param>
/// <param name="ReviewCoverage">For a review: the agent's coverage per criterion, with the criterion's current text.</param>
/// <param name="Excerpt">The run's last transcript events before the item was raised (for a failure: the run's last events).</param>
/// <param name="ChangeSetId">For a review: the submitted work, opened on the review screen (null for reviews from before change sets).</param>
public sealed record AttentionItemInfo(
    Guid Id,
    AttentionKind Kind,
    AttentionStatus Status,
    bool Blocking,
    Guid? RunId,
    string ProjectSlug,
    int TicketNumber,
    string TicketKey,
    string TicketTitle,
    ItemRun? Run,
    QuestionPayload? Question,
    PlanPayload? Plan,
    ReviewPayload? Review,
    IReadOnlyList<ReviewCoverage> ReviewCoverage,
    FailurePayload? Failure,
    AttentionAnswer? Answer,
    IReadOnlyList<RunEventInfo> Excerpt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AnsweredAt,
    Guid? ChangeSetId);

/// <param name="ProgressStep">What the agent last reported with report_progress.</param>
public sealed record ItemRun(AgentRunState State, string? Adapter, string? Model, string? ProgressStep, string? ProgressNote, int Attempt);

/// <param name="Text">The criterion's text; null when it was deleted since.</param>
public sealed record ReviewCoverage(Guid CriterionId, string? Text, CriterionCoverage Status, string? Note);

/// <summary>
/// A question needs <see cref="Text"/> (an option is answered with its text). A plan needs <see cref="Decision"/>;
/// <see cref="Text"/> holds the notes and is required to send a plan back for revision.
/// </summary>
public sealed record AnswerRequest(string? Text = null, PlanDecision? Decision = null);
