using Tikai.Core.Agents;

namespace Tikai.Core.Runs;

/// <summary>How a run's cost grows as its containers end.</summary>
public static class RunCost
{
    /// <summary>
    /// The run's cost after a container ended: the session's reported total on top of the earlier sessions' cost
    /// (Claude Code's total already covers the session's earlier containers, also one stopped before it reported), and
    /// the container's tokens added to the run's. Without a report the stored values stay.
    /// </summary>
    public static (decimal? Usd, long? Tokens) After(AgentRun run, AgentCost? container) =>
        (container?.Usd is { } session ? (run.CostUsdBeforeSession ?? 0) + session : run.CostUsd,
         container?.Tokens is { } tokens ? (run.CostTokens ?? 0) + tokens : run.CostTokens);
}
