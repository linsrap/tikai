using Tikai.Core.Common;

namespace Tikai.Core.Runs;

/// <summary>
/// One attempt of an agent at one ticket. The agent reaches the hub over MCP with a run token
/// (see <see cref="RunToken"/>) that only grants access to this run's ticket and project.
/// States change only along <see cref="RunStateMachine"/>.
/// </summary>
public class AgentRun : Entity
{
    /// <summary>Event topic for changes to a run (progress, state).</summary>
    public const string Topic = "agent_runs";

    public Guid TicketId { get; set; }

    /// <summary>The repository the run works on; its default branch is the base of the run branch.</summary>
    public Guid? RepositoryId { get; set; }

    /// <summary>Whose API key the agent uses; null once that provider was deleted (<see cref="Model"/> stays).</summary>
    public Guid? LlmProviderId { get; set; }

    /// <summary>The person who assigned the ticket to the agent.</summary>
    public Guid? StartedById { get; set; }

    public AgentRunState State { get; set; }

    /// <summary>SHA-256 of the run token. The token itself is handed out once and never stored.</summary>
    public required byte[] TokenHash { get; set; }

    /// <summary>The adapter that runs the agent, e.g. "claude-code".</summary>
    public string? Adapter { get; set; }

    public string? Model { get; set; }

    /// <summary>tikai/&lt;KEY-N&gt;-&lt;run8&gt;, see <see cref="RunBranch"/>.</summary>
    public string? Branch { get; set; }

    /// <summary>The agent container of the current attempt; null before it was created and after it was removed.</summary>
    public string? ContainerId { get; set; }

    /// <summary>The agent's own session id (Claude Code's, from its init event), for a later resume.</summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// Lines of the container log (stdout and stderr) already turned into events. After a hub restart the log is read
    /// again from the start and this many lines are skipped, so nothing is stored twice.
    /// </summary>
    public long LogLinesRead { get; set; }

    /// <summary>The highest <see cref="RunEvent.Seq"/> handed out; events take their numbers from here.</summary>
    public long LastEventSeq { get; set; }

    /// <summary>The step the agent last reported with report_progress.</summary>
    public string? ProgressStep { get; set; }

    public string? ProgressNote { get; set; }

    public DateTimeOffset? ProgressAt { get; set; }

    public int? ExitCode { get; set; }

    /// <summary>Why the run failed, for people.</summary>
    public string? FailureReason { get; set; }

    public decimal? CostUsd { get; set; }

    /// <summary>Input plus output tokens as the agent reported them.</summary>
    public long? CostTokens { get; set; }

    public DateTimeOffset? CancelRequestedAt { get; set; }

    /// <summary>When the agent container started.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }
}

/// <summary>Run states; <see cref="RunStateMachine"/> says which follow which.</summary>
public enum AgentRunState
{
    Queued,
    Preparing,
    Running,
    Blocked,
    InReview,
    ChangesRequested,
    Merged,
    Failed,
    Cancelled,
}

public static class AgentRunStates
{
    /// <summary>An ended run's token no longer works.</summary>
    public static bool IsEnded(this AgentRunState state) =>
        state is AgentRunState.Merged or AgentRunState.Failed or AgentRunState.Cancelled;

    /// <summary>The states <see cref="IsEnded"/> is true for, e.g. for a query.</summary>
    public static IReadOnlyList<AgentRunState> Ended { get; } =
        [.. Enum.GetValues<AgentRunState>().Where(s => s.IsEnded())];
}

public static class RunRules
{
    public const int MaxProgressStepLength = 200;
    public const int MaxProgressNoteLength = 2_000;
    public const int MaxFailureReasonLength = 2_000;

    /// <summary>The only adapter in Phase 1.</summary>
    public const string ClaudeCodeAdapter = "claude-code";
}
