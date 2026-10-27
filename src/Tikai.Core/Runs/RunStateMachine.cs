namespace Tikai.Core.Runs;

/// <summary>What happened to a run; <see cref="RunStateMachine.Next"/> turns it into the next state.</summary>
public enum RunTrigger
{
    /// <summary>The start job took the run.</summary>
    Prepare,

    /// <summary>The agent container started.</summary>
    Start,

    /// <summary>The agent waits for a human (a blocking item is open).</summary>
    Block,

    /// <summary>The answer came; the agent continues.</summary>
    Unblock,

    /// <summary>The agent submitted its work for review.</summary>
    Submit,

    /// <summary>The reviewer asked for changes.</summary>
    RequestChanges,

    /// <summary>The agent picks up the requested changes.</summary>
    Resume,

    /// <summary>The work was merged.</summary>
    Merge,

    Fail,
    Cancel,
}

/// <summary>
/// The run lifecycle: queued → preparing → running ⇄ blocked → in_review → (changes_requested → running) →
/// merged | failed | cancelled. Fail and cancel work from every state that has not ended.
/// </summary>
public static class RunStateMachine
{
    /// <summary>The state after the trigger, or null when the trigger does not apply in that state.</summary>
    public static AgentRunState? Next(AgentRunState state, RunTrigger trigger) => (state, trigger) switch
    {
        _ when state.IsEnded() => null,
        (_, RunTrigger.Fail) => AgentRunState.Failed,
        (_, RunTrigger.Cancel) => AgentRunState.Cancelled,
        (AgentRunState.Queued, RunTrigger.Prepare) => AgentRunState.Preparing,
        (AgentRunState.Preparing, RunTrigger.Start) => AgentRunState.Running,
        (AgentRunState.Running, RunTrigger.Block) => AgentRunState.Blocked,
        (AgentRunState.Blocked, RunTrigger.Unblock) => AgentRunState.Running,
        (AgentRunState.Running, RunTrigger.Submit) => AgentRunState.InReview,
        (AgentRunState.InReview, RunTrigger.RequestChanges) => AgentRunState.ChangesRequested,
        (AgentRunState.ChangesRequested, RunTrigger.Resume) => AgentRunState.Running,
        (AgentRunState.InReview, RunTrigger.Merge) => AgentRunState.Merged,
        _ => null,
    };

    /// <summary>The states a trigger can start from, e.g. for a compare-and-set update.</summary>
    public static IReadOnlyList<AgentRunState> From(RunTrigger trigger) =>
        [.. Enum.GetValues<AgentRunState>().Where(s => Next(s, trigger) is not null)];
}
