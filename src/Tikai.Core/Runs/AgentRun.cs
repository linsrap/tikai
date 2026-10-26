using Tikai.Core.Common;

namespace Tikai.Core.Runs;

/// <summary>
/// One attempt of an agent at one ticket. The agent reaches the hub over MCP with a run token
/// (see <see cref="RunToken"/>) that only grants access to this run's ticket and project.
/// </summary>
public class AgentRun : Entity
{
    /// <summary>Event topic for changes to a run (progress, state).</summary>
    public const string Topic = "agent_runs";

    public Guid TicketId { get; set; }

    public AgentRunState State { get; set; }

    /// <summary>SHA-256 of the run token. The token itself is handed out once and never stored.</summary>
    public required byte[] TokenHash { get; set; }

    /// <summary>The step the agent last reported with report_progress.</summary>
    public string? ProgressStep { get; set; }

    public string? ProgressNote { get; set; }

    public DateTimeOffset? ProgressAt { get; set; }
}

/// <summary>Run states; the transitions between them come with the orchestrator (roadmap step 10).</summary>
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
}
