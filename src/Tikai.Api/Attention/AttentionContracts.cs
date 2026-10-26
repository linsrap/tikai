using Tikai.Core.Attention;

namespace Tikai.Api.Attention;

/// <summary>
/// An inbox item. Exactly one of <see cref="Question"/>, <see cref="Plan"/> and <see cref="Review"/> is set,
/// matching <see cref="Kind"/>.
/// </summary>
/// <param name="TicketKey">E.g. "HUB-42".</param>
public sealed record AttentionItemInfo(
    Guid Id,
    AttentionKind Kind,
    AttentionStatus Status,
    Guid? RunId,
    string ProjectSlug,
    int TicketNumber,
    string TicketKey,
    QuestionPayload? Question,
    PlanPayload? Plan,
    ReviewPayload? Review,
    AttentionAnswer? Answer,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AnsweredAt);

/// <summary>
/// A question needs <see cref="Text"/> (an option is answered with its text). A plan needs <see cref="Decision"/>;
/// <see cref="Text"/> holds the notes and is required to send a plan back for revision.
/// </summary>
public sealed record AnswerRequest(string? Text = null, PlanDecision? Decision = null);
