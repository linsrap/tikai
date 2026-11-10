using Tikai.Api.Runs;
using Tikai.Core.Attention;
using Tikai.Core.Reviews;
using Tikai.Core.Runs;

namespace Tikai.Api.Attention;

/// <summary>
/// An inbox item with what a person needs to act on it without opening anything else. Exactly one of
/// <see cref="Question"/>, <see cref="Plan"/>, <see cref="Review"/>, <see cref="Failure"/> and <see cref="Conflict"/>
/// is set, matching <see cref="Kind"/>.
/// </summary>
/// <param name="TicketKey">E.g. "HUB-42".</param>
/// <param name="Blocking">An agent waits for it (an open question or plan); these sort first in the inbox.</param>
/// <param name="Run">The run that raised it, as it is now.</param>
/// <param name="ReviewCoverage">For a review: the agent's coverage per criterion, with the criterion's current text.</param>
/// <param name="Excerpt">The run's last transcript events before the item was raised (for a failure: the run's last events).</param>
/// <param name="ChangeSetId">For a review: the submitted work, opened on the review screen (null for reviews from before change sets).</param>
/// <param name="Analysis">For a review: where the analysis of the change set's latest round stands.</param>
/// <param name="Round">For a review: the change set's latest round and how the agent answered the comments sent to it.</param>
/// <param name="Conflict">For a conflict: the approved round, the default branch and the files that conflict.</param>
/// <param name="DeliveryError">For a review: why an earlier approval did not get through (it came back to the inbox).</param>
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
    Guid? ChangeSetId,
    AnalysisBrief? Analysis,
    RoundBrief? Round = null,
    ConflictPayload? Conflict = null,
    string? DeliveryError = null);

/// <param name="Answered">Threads with comments sent to the agent that it replied to after the last one.</param>
/// <param name="Unanswered">Such threads without a reply since.</param>
public sealed record RoundBrief(int RoundNo, int Answered, int Unanswered);

/// <summary>A review analysis in one line: counts per coverage status and high-risk files (zero until it is done).</summary>
/// <param name="Criteria">The ticket's acceptance criteria now; the ones the analysis did not assess count as not met.</param>
/// <param name="CloseLook">Files that need a close look: in <see cref="LookAt"/> or with a high risk.</param>
/// <param name="Headline">The analysis' headline; null until it is done, and for analyses from before v2.</param>
public sealed record AnalysisBrief(
    AnalysisStatus Status,
    int Covered,
    int Partial,
    int Missing,
    int HighRisk,
    string? Error,
    int Criteria = 0,
    int CloseLook = 0,
    string? Headline = null,
    IReadOnlyList<LookAt>? LookAt = null);

/// <param name="ProgressStep">What the agent last reported with report_progress.</param>
public sealed record ItemRun(AgentRunState State, string? Adapter, string? Model, string? ProgressStep, string? ProgressNote, int Attempt);

/// <param name="Text">The criterion's text; null when it was deleted since.</param>
public sealed record ReviewCoverage(Guid CriterionId, string? Text, CriterionCoverage Status, string? Note);

/// <summary>
/// A question needs <see cref="Text"/> (an option is answered with its text). A plan needs <see cref="Decision"/>;
/// <see cref="Text"/> holds the notes and is required to send a plan back for revision.
/// </summary>
public sealed record AnswerRequest(string? Text = null, PlanDecision? Decision = null);
