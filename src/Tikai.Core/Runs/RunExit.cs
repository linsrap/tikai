using System.Globalization;

namespace Tikai.Core.Runs;

/// <summary>The agent's own final report (Claude Code's result event).</summary>
public sealed record AgentResult(bool IsError, string? Subtype);

/// <summary>What the hub knows when an agent container has ended.</summary>
/// <param name="HubStopReason">Set when the hub stopped the container itself, e.g. because the MCP server was not connected.</param>
/// <param name="Result">Null when the agent printed no result.</param>
/// <param name="HasOpenBlockingItem">A question or plan of the run is still waiting for a human.</param>
/// <param name="Submitted">The agent called submit_for_review.</param>
/// <param name="HasUndeliveredAnswer">A question or plan was answered, but the answer never reached the agent.</param>
/// <param name="Attempt">How this container took up the run.</param>
public sealed record ExitFacts(
    bool CancelRequested,
    string? HubStopReason,
    bool OomKilled,
    long ExitCode,
    AgentResult? Result,
    bool HasOpenBlockingItem,
    bool Submitted,
    bool HasUndeliveredAnswer = false,
    RunAttemptKind Attempt = RunAttemptKind.Start);

/// <param name="RetryWithSummary">
/// The resumed session failed: instead of failing the run, a fresh session continues it with a summary (once).
/// </param>
public sealed record ExitDecision(RunTrigger Trigger, string? Reason, bool RetryWithSummary = false);

/// <summary>Maps a container's end to a run transition. The exit code alone says little: Claude Code exits 0 also when it gave up.</summary>
public static class RunExit
{
    public static ExitDecision Map(ExitFacts facts)
    {
        if (facts.CancelRequested)
        {
            return new(RunTrigger.Cancel, null);
        }
        if (facts.HubStopReason is { } reason)
        {
            return new(RunTrigger.Fail, reason);
        }
        if (facts.OomKilled)
        {
            return new(RunTrigger.Fail, "The agent ran out of memory.");
        }
        // The agent stopped waiting for an answer (or was stopped while waiting), or an answer came too late for it:
        // a new container continues once everything is answered.
        if (facts.HasOpenBlockingItem || facts.HasUndeliveredAnswer)
        {
            return new(RunTrigger.Block, null);
        }
        var succeeded = facts.ExitCode == 0 && facts.Result is { IsError: false };
        if (succeeded && facts.Submitted)
        {
            return new(RunTrigger.Submit, null);
        }
        if (succeeded)
        {
            return new(RunTrigger.Fail, "The agent ended without calling submit_for_review.");
        }
        var failure = facts.Result is { IsError: true } result
            ? $"The agent ended with an error ({result.Subtype ?? "no details"}, exit code {Code(facts.ExitCode)})."
            : $"The agent exited with code {Code(facts.ExitCode)} without a result.";
        return new(RunTrigger.Fail, failure, RetryWithSummary: facts.Attempt == RunAttemptKind.Resume);
    }

    private static string Code(long exitCode) => exitCode.ToString(CultureInfo.InvariantCulture);
}
