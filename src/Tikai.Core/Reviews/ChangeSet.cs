using Tikai.Core.Common;

namespace Tikai.Core.Reviews;

/// <summary>
/// The hub's own "pull request": a run's work, fetched into the mirror when the agent submits it. One per run; every
/// submission adds a <see cref="ReviewRound"/>, and base and head follow the latest one.
/// </summary>
public class ChangeSet : Entity
{
    /// <summary>Event topic for new change sets, new rounds and verdicts.</summary>
    public const string Topic = "change_sets";

    public Guid TicketId { get; set; }

    public Guid RunId { get; set; }

    public Guid RepositoryId { get; set; }

    /// <summary>Where the run branch forked from the default branch (merge base), as of the latest round.</summary>
    public required string BaseSha { get; set; }

    public required string HeadSha { get; set; }

    public ChangeSetStatus Status { get; set; }
}

public enum ChangeSetStatus
{
    Open,
    Merged,
    Closed,
}

/// <summary>One submission of a change set; the reviewer's verdict is given per round.</summary>
public class ReviewRound : Entity
{
    public Guid ChangeSetId { get; set; }

    /// <summary>1, 2, 3… per change set.</summary>
    public int RoundNo { get; set; }

    /// <summary>The merge base of this round's head with the default branch (a rebase changes it).</summary>
    public required string BaseSha { get; set; }

    public required string HeadSha { get; set; }

    /// <summary>JSON of the agent's <see cref="Attention.ReviewPayload"/>.</summary>
    public required string SelfReport { get; set; }

    /// <summary>Files the agent left uncommitted when it submitted; they are not part of the round.</summary>
    public string[] Uncommitted { get; set; } = [];

    public ReviewVerdict? Verdict { get; set; }

    public Guid? VerdictById { get; set; }

    public DateTimeOffset? VerdictAt { get; set; }

    /// <summary>Null for rounds from before analyses existed.</summary>
    public AnalysisStatus? AnalysisStatus { get; set; }

    /// <summary>JSON of the <see cref="ReviewAnalysis"/> once it is done.</summary>
    public string? Analysis { get; set; }

    /// <summary>Why the analysis failed, for people.</summary>
    public string? AnalysisError { get; set; }

    /// <summary>When the analysis ended (done or failed).</summary>
    public DateTimeOffset? AnalysisAt { get; set; }
}

public enum ReviewVerdict
{
    Approve,
    RequestChanges,
    Reject,
}

/// <summary>
/// A person marked a file of a change set as viewed. <see cref="ViewedSha"/> is the file's blob id at that time
/// (<see cref="ReviewRules.DeletedBlob"/> for a deleted file): the file counts as viewed while its blob id in the round
/// shown is the same, so a later round that changes it shows it unviewed again, without any reset.
/// </summary>
public class FileViewState : Entity
{
    public Guid UserId { get; set; }

    public Guid ChangeSetId { get; set; }

    public required string FilePath { get; set; }

    public required string ViewedSha { get; set; }
}

public static class ReviewRules
{
    public const int MaxFilePathLength = 1_000;

    /// <summary>The blob id git shows for the missing side of an added or deleted file.</summary>
    public const string DeletedBlob = "0000000000000000000000000000000000000000";

    /// <summary>What "viewed" is compared with: the new blob, or the zero id when the file was deleted.</summary>
    public static string ViewKey(string? newBlob) => newBlob ?? DeletedBlob;

    /// <summary>The ref that keeps a round's head in the mirror, also after the run branch was rewritten.</summary>
    public static string RoundRef(Guid changeSetId, int roundNo) => $"refs/tikai/rounds/{changeSetId:D}/{roundNo}";
}
