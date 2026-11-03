using Tikai.Core.Common;

namespace Tikai.Core.Reviews;

/// <summary>
/// A discussion on a review round: anchored to a line range on one side of a file, or general (no anchor). Its
/// comments are notes for humans or are sent to the agent with the next "request changes".
/// </summary>
public class ReviewThread : Entity
{
    public Guid ChangeSetId { get; set; }

    /// <summary>The round the thread was started in; its lines refer to that round's base (old) or head (new).</summary>
    public Guid ReviewRoundId { get; set; }

    /// <summary>Null for a general comment on the round.</summary>
    public string? FilePath { get; set; }

    public DiffSide? Side { get; set; }

    public int? LineStart { get; set; }

    public int? LineEnd { get; set; }

    public ThreadStatus Status { get; set; }

    public Guid? CreatedById { get; set; }

    public Guid? ResolvedById { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }
}

/// <summary>A comment in a <see cref="ReviewThread"/>, by a person or by the run's agent (exactly one).</summary>
public class ReviewComment : Entity
{
    public Guid ThreadId { get; set; }

    public Guid? AuthorUserId { get; set; }

    public Guid? AuthorRunId { get; set; }

    /// <summary>Markdown.</summary>
    public required string Body { get; set; }

    public CommentAction Action { get; set; }

    /// <summary>When a "send to agent" comment was handed to the agent (with a request for changes); then it is fixed.</summary>
    public DateTimeOffset? SentAt { get; set; }

    public DateTimeOffset? EditedAt { get; set; }
}

/// <summary>The side of a diff: old is the round's base, new its head.</summary>
public enum DiffSide
{
    Old,
    New,
}

public enum ThreadStatus
{
    Open,
    Resolved,
}

public enum CommentAction
{
    /// <summary>For humans only; the agent never sees it.</summary>
    Note,

    /// <summary>Handed to the agent when someone requests changes.</summary>
    SendToAgent,
}

/// <summary>A thread's anchor as given by a person; checked by <see cref="ThreadRules.Anchor"/>.</summary>
public sealed record ThreadAnchor(string Path, DiffSide Side, int LineStart, int LineEnd);

public static class ThreadRules
{
    public const int MaxBodyLength = 10_000;

    /// <summary>The trimmed body, or an error for people (and agents).</summary>
    public static (string? Body, string? Error) Body(string? body)
    {
        var text = body?.Trim() ?? "";
        return text.Length is 0 or > MaxBodyLength
            ? (null, $"A comment has 1 to {MaxBodyLength} characters.")
            : (text, null);
    }

    /// <summary>
    /// Checks an anchor: all of path, side and lines, or none of them (a general comment). Lines are 1-based with
    /// start ≤ end. Whether the side exists and how long it is is checked against the diff by the caller.
    /// </summary>
    public static (ThreadAnchor? Anchor, string? Error) Anchor(string? path, DiffSide? side, int? lineStart, int? lineEnd)
    {
        if (path is null && side is null && lineStart is null && lineEnd is null)
        {
            return (null, null);
        }
        if (string.IsNullOrEmpty(path) || side is null || lineStart is null)
        {
            return (null, "Give path, side and lines together, or none of them for a general comment.");
        }
        var end = lineEnd ?? lineStart.Value;
        if (lineStart < 1 || end < lineStart)
        {
            return (null, "Lines start at 1 and the range ends at or after its start.");
        }
        return (new ThreadAnchor(path, side.Value, lineStart.Value, end), null);
    }
}
