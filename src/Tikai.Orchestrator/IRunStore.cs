using Tikai.Core.Agents;
using Tikai.Core.Llm;
using Tikai.Core.Runs;

namespace Tikai.Orchestrator;

/// <summary>
/// What the orchestrator needs from the hub's database (implemented in Api). Every state change is a compare-and-set
/// along <see cref="RunStateMachine"/>, so a cancel and a container end arriving together cannot both win.
/// </summary>
public interface IRunStore
{
    /// <summary>queued → preparing.</summary>
    Task<PrepareClaim> ClaimForPreparingAsync(Guid runId, CancellationToken cancellationToken);

    /// <exception cref="RunStartException">The run cannot start, for a reason people can fix.</exception>
    Task<RunStartInfo> GetStartInfoAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>A fresh token for a new container; null when the run has ended meanwhile.</summary>
    Task<string?> RenewTokenAsync(Guid runId, CancellationToken cancellationToken);

    Task SetContainerAsync(Guid runId, string? containerId, CancellationToken cancellationToken);

    /// <summary>preparing → running. False when the run was cancelled meanwhile.</summary>
    Task<bool> MarkRunningAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>Appends events (numbered in order) and, when given, the number of log lines they cover.</summary>
    Task AppendEventsAsync(Guid runId, IReadOnlyList<ParsedEvent> events, long? logLinesRead, CancellationToken cancellationToken);

    Task SetSessionAsync(Guid runId, string sessionId, CancellationToken cancellationToken);

    /// <summary>What the database knows when a container ends.</summary>
    Task<RunEndContext> GetEndContextAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>
    /// Applies the trigger (state, ended_at, ticket status, a failure item when it fails) and forgets the container.
    /// False when the trigger no longer applies (the run moved on).
    /// </summary>
    Task<bool> EndAsync(Guid runId, RunEnd outcome, CancellationToken cancellationToken);

    /// <summary>Runs whose agent container should be followed (state running, container known).</summary>
    Task<IReadOnlyList<WatchedRun>> ListRunningAsync(CancellationToken cancellationToken);

    /// <summary>Marks the run as to be cancelled and returns its state and container.</summary>
    Task<CancelTarget?> RequestCancelAsync(Guid runId, CancellationToken cancellationToken);
}

public enum PrepareClaim
{
    Claimed,

    /// <summary>The run is already preparing: the hub stopped during an earlier attempt.</summary>
    Interrupted,

    /// <summary>The run is past preparing (or was cancelled); nothing to do.</summary>
    NotQueued,
}

/// <param name="MirrorPath">The repository's bare copy on the hub; the run clone is made from its default branch.</param>
public sealed record RunStartInfo(
    Guid RunId,
    string TicketKey,
    string TicketTitle,
    string TicketDocument,
    string Branch,
    string DefaultBranch,
    string MirrorPath,
    LlmConnection Llm)
{
    public override string ToString() => $"RunStartInfo {{ RunId = {RunId}, TicketKey = {TicketKey}, Branch = {Branch}, Llm = {Llm} }}";
}

/// <summary>A run cannot start; the message is for people and becomes the failure reason.</summary>
public sealed class RunStartException(string message) : Exception(message);

/// <param name="Secrets">Values to remove from the agent's output.</param>
public sealed record WatchedRun(Guid RunId, string ContainerId, long LogLinesRead, long LastEventSeq, IReadOnlyList<string> Secrets)
{
    public override string ToString() => $"WatchedRun {{ RunId = {RunId}, ContainerId = {ContainerId}, LogLinesRead = {LogLinesRead} }}";
}

public sealed record RunEndContext(bool CancelRequested, bool HasOpenBlockingItem, bool Submitted);

public sealed record RunEnd(RunTrigger Trigger, string? Reason, int? ExitCode = null, AgentCost? Cost = null);

public sealed record CancelTarget(AgentRunState State, string? ContainerId);
