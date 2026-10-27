using Tikai.Core.Common;

namespace Tikai.Core.Runs;

/// <summary>
/// One entry of a run's transcript: what the agent said and did, and what the hub did with the run.
/// Append-only; <see cref="Seq"/> numbers a run's events 1, 2, 3… so a reader can continue after the last one it saw.
/// </summary>
public class RunEvent : Entity
{
    /// <summary>Event topic; the published id is the run's, not the event's.</summary>
    public const string Topic = "run_events";

    public Guid RunId { get; set; }

    public long Seq { get; set; }

    public RunEventKind Kind { get; set; }

    /// <summary>JSON object, trimmed by <see cref="RunEventPayload"/> and free of the run's secrets.</summary>
    public required string Payload { get; set; }
}

public enum RunEventKind
{
    /// <summary>The agent's start: session, model, tools, MCP server status.</summary>
    System,

    /// <summary>Text (or thinking) of the agent.</summary>
    Message,

    ToolCall,
    ToolResult,

    /// <summary>A report_progress call.</summary>
    Progress,

    /// <summary>The agent's final report: outcome, turns, duration, cost.</summary>
    Result,

    /// <summary>Everything else: stderr, other agent output, the hub's own notes on the run.</summary>
    Log,
}
