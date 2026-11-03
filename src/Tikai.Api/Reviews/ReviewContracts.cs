using Tikai.Api.Attention;
using Tikai.Api.Tickets;
using Tikai.Core.Attention;
using Tikai.Core.Git;
using Tikai.Core.Reviews;
using Tikai.Core.Runs;
using Tikai.Review;

namespace Tikai.Api.Reviews;

/// <summary>A change set with its ticket, run and rounds (oldest first), for the review screen.</summary>
/// <param name="Criteria">The ticket's acceptance criteria as they are now.</param>
public sealed record ChangeSetInfo(
    Guid Id,
    ChangeSetStatus Status,
    string ProjectSlug,
    int TicketNumber,
    string TicketKey,
    string TicketTitle,
    Guid RunId,
    AgentRunState RunState,
    string? Model,
    string Branch,
    string RepositoryName,
    string DefaultBranch,
    IReadOnlyList<CriterionInfo> Criteria,
    IReadOnlyList<ReviewRoundInfo> Rounds,
    DateTimeOffset CreatedAt);

/// <param name="Coverage">The agent's coverage per criterion, with the criterion's current text.</param>
/// <param name="Uncommitted">git status lines of files the agent left uncommitted; they are not part of the round.</param>
public sealed record ReviewRoundInfo(
    int RoundNo,
    string BaseSha,
    string HeadSha,
    ReviewPayload SelfReport,
    IReadOnlyList<ReviewCoverage> Coverage,
    IReadOnlyList<string> Uncommitted,
    ReviewVerdict? Verdict,
    DateTimeOffset? VerdictAt,
    DateTimeOffset CreatedAt,
    AnalysisInfo? Analysis);

/// <summary>The LLM's analysis of a round; null for rounds from before analyses existed.</summary>
/// <param name="Error">Why it failed, for people.</param>
/// <param name="At">When it was done or failed.</param>
/// <param name="Result">Set when it is done.</param>
public sealed record AnalysisInfo(AnalysisStatus Status, string? Error, DateTimeOffset? At, ReviewAnalysis? Result);

/// <param name="Truncated">The change has more files than are listed.</param>
/// <param name="View">What is listed: the full change, or the interdiff since the round before.</param>
/// <param name="Interdiff">How the interdiff was computed (none for the full view and the first round).</param>
/// <param name="FromSha">The commit of the diff's old side: the base, or the previous round's head.</param>
/// <param name="ToSha">The commit of the diff's new side: the round's head.</param>
/// <param name="PreviousRound">The round before this one, if any.</param>
/// <param name="PreviousBaseSha">Its base; differs from <paramref name="BaseSha"/> when the agent rebased.</param>
/// <param name="Dropped">With a changed base: files the round before changed and this one no longer does.</param>
public sealed record ReviewFilesInfo(
    int RoundNo,
    string BaseSha,
    string HeadSha,
    IReadOnlyList<ReviewFileInfo> Files,
    bool Truncated,
    ReviewView View,
    InterdiffKind Interdiff,
    string FromSha,
    string ToSha,
    int? PreviousRound,
    string? PreviousBaseSha,
    IReadOnlyList<string> Dropped);

/// <summary>A changed file of a round.</summary>
/// <param name="Path">The path it has now (or had, when deleted).</param>
/// <param name="OldPath">Where it was before a rename.</param>
/// <param name="Additions">Changed lines; null for binary files.</param>
/// <param name="Generated">Generated or build output: collapsed in the review.</param>
/// <param name="Large">Too many changed lines to show by default.</param>
/// <param name="Viewed">The current user marked this version of the file as viewed.</param>
/// <param name="Language">Monaco's language id for highlighting.</param>
public sealed record ReviewFileInfo(
    string Path,
    string? OldPath,
    GitChangeKind Kind,
    int? Additions,
    int? Deletions,
    bool Binary,
    bool Lock,
    bool Generated,
    bool Large,
    bool Viewed,
    string Language);

/// <summary>Both sides of a file; a side is null where the file does not exist (added, deleted).</summary>
public sealed record FileContentsInfo(string Path, string? OldPath, FileSide? Old, FileSide? New, string Language);

/// <param name="Text">Null when the side is binary or too large to show.</param>
public sealed record FileSide(long Size, string? Text, bool Binary, bool TooLarge);

/// <param name="Round">The round the reviewer looks at (default: the latest).</param>
/// <param name="Full">The file is listed in the whole change, not in the interdiff (the default).</param>
public sealed record ViewedRequest(string Path, bool Viewed, int? Round = null, bool Full = false);

public sealed record ViewedInfo(string Path, bool Viewed);

/// <param name="Message">With request_changes: a general comment sent to the agent with the line comments.</param>
public sealed record VerdictRequest(ReviewVerdict Verdict, string? Message = null);

/// <summary>A review thread with its comments, oldest first.</summary>
/// <param name="RoundNo">The round it was started in; its lines refer to that round's base (old) or head (new).</param>
/// <param name="Path">Null for a general comment.</param>
/// <param name="ResolvedBy">Who resolved it (display name).</param>
public sealed record ReviewThreadInfo(
    Guid Id,
    int RoundNo,
    string? Path,
    DiffSide? Side,
    int? LineStart,
    int? LineEnd,
    ThreadStatus Status,
    DateTimeOffset CreatedAt,
    string? ResolvedBy,
    DateTimeOffset? ResolvedAt,
    IReadOnlyList<ReviewCommentInfo> Comments);

/// <param name="AuthorName">A person's display name, or "Agent".</param>
/// <param name="SentAt">When a "send to agent" comment was handed to the agent.</param>
/// <param name="CanEdit">The caller wrote it and it was not sent yet.</param>
public sealed record ReviewCommentInfo(
    Guid Id,
    CommentAuthorKind Author,
    string AuthorName,
    string Body,
    CommentAction Action,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EditedAt,
    DateTimeOffset? SentAt,
    bool CanEdit);

public enum CommentAuthorKind
{
    Person,
    Agent,
}

/// <summary>A new thread on a round with its first comment; path, side and lines together, or none for a general comment.</summary>
/// <param name="LineEnd">Defaults to <paramref name="LineStart"/>.</param>
public sealed record CreateThreadRequest(
    int RoundNo,
    string Body,
    CommentAction Action,
    string? Path = null,
    DiffSide? Side = null,
    int? LineStart = null,
    int? LineEnd = null);

public sealed record CommentRequest(string Body, CommentAction Action);
