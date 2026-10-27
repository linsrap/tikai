using Tikai.Core.Agents;
using Tikai.Core.Attention;
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

    /// <summary>Forgets the run's container (null) after it was removed.</summary>
    Task SetContainerAsync(Guid runId, string? containerId, CancellationToken cancellationToken);

    /// <summary>A new agent container takes over the run: its log starts at line 0, and it is the next attempt.</summary>
    Task AttachContainerAsync(Guid runId, string containerId, RunAttemptKind kind, CancellationToken cancellationToken);

    /// <summary>preparing → running. False when the run was cancelled meanwhile.</summary>
    Task<bool> MarkRunningAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>What a resume needs: whether the run can be resumed now, the session, and the answers.</summary>
    Task<ResumeInfo?> GetResumeInfoAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>
    /// The resumed container started: blocked → running (a summary after a failed resume is running already), and the
    /// answers in its prompt count as delivered. False when the run was cancelled or moved on meanwhile.
    /// </summary>
    Task<bool> MarkResumedAsync(Guid runId, string containerId, IReadOnlyList<Guid> deliveredItems, CancellationToken cancellationToken);

    /// <summary>Appends events (numbered in order) and, when given, the number of log lines they cover.</summary>
    Task AppendEventsAsync(Guid runId, IReadOnlyList<ParsedEvent> events, long? logLinesRead, CancellationToken cancellationToken);

    Task SetSessionAsync(Guid runId, string sessionId, CancellationToken cancellationToken);

    /// <summary>What the database knows when a container ends.</summary>
    Task<RunEndContext> GetEndContextAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>
    /// Applies the trigger (state, ended_at, ticket status, a failure item when it fails; open questions of an ended run
    /// are dismissed) and forgets the container. Only while the run still has <see cref="RunEnd.ContainerId"/>: an old
    /// container's end never changes a run a newer one took over. <see cref="RunTrigger.Block"/> on a blocked run keeps
    /// it blocked without a container and asks for a resume once everything is answered.
    /// False when the trigger no longer applies (the run moved on).
    /// </summary>
    Task<bool> EndAsync(Guid runId, RunEnd outcome, CancellationToken cancellationToken);

    /// <summary>The resumed session failed: the run stays running without a container, and a fresh session takes over.</summary>
    Task<bool> RetryWithSummaryAsync(Guid runId, string containerId, string reason, CancellationToken cancellationToken);

    /// <summary>Every run that has not ended, with what the reconciler needs to know about it.</summary>
    Task<IReadOnlyList<ActiveRun>> ListActiveAsync(CancellationToken cancellationToken);

    /// <summary>Fails a run nobody works on any more (no live job), if it is still in that state with that container.</summary>
    Task<bool> FailAbandonedAsync(Guid runId, AgentRunState state, string? containerId, string reason, CancellationToken cancellationToken);

    /// <summary>Enqueues a resume when the run is blocked, has no container, and nothing is left unanswered.</summary>
    Task<bool> RequestResumeAsync(Guid runId, CancellationToken cancellationToken);

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

/// <param name="CanResume">Blocked without a container and nothing open (or, for a summary, running without one), not cancelled.</param>
/// <param name="ToDeliver">The answers the agent has not got yet (ids and content), oldest first.</param>
/// <param name="Answered">Every answered question and plan of the run, oldest first (for a summary).</param>
/// <param name="ApprovedPlan">The latest plan the human approved.</param>
public sealed record ResumeInfo(
    AgentRunState State,
    bool CanResume,
    bool CancelRequested,
    string? SessionId,
    IReadOnlyList<(Guid Id, AnsweredRequest Answer)> ToDeliver,
    IReadOnlyList<AnsweredRequest> Answered,
    PlanPayload? ApprovedPlan);

/// <summary>A run cannot start; the message is for people and becomes the failure reason.</summary>
public sealed class RunStartException(string message) : Exception(message);

/// <param name="Secrets">Values to remove from the agent's output.</param>
public sealed record WatchedRun(Guid RunId, string ContainerId, long LogLinesRead, long LastEventSeq, IReadOnlyList<string> Secrets)
{
    public override string ToString() => $"WatchedRun {{ RunId = {RunId}, ContainerId = {ContainerId}, LogLinesRead = {LogLinesRead} }}";
}

/// <param name="HasUndeliveredAnswer">The latest blocking item is answered, but the answer never reached the agent.</param>
public sealed record RunEndContext(bool CancelRequested, bool HasOpenBlockingItem, bool Submitted, bool HasUndeliveredAnswer = false, RunAttemptKind Attempt = RunAttemptKind.Start);

/// <param name="ContainerId">The container that ended; null for a run without one (e.g. cancelling a blocked run).</param>
public sealed record RunEnd(RunTrigger Trigger, string? Reason, int? ExitCode = null, AgentCost? Cost = null, string? ContainerId = null);

public sealed record CancelTarget(AgentRunState State, string? ContainerId);

/// <summary>A run that has not ended, as the reconciler sees it.</summary>
/// <param name="HasLiveJob">A runs job (start, resume, summary) is pending or being worked on.</param>
/// <param name="LastJobError">The error of the run's last dead job, if any.</param>
/// <param name="WaitingFor">How long the oldest open blocking item has been open, by the database's clock.</param>
public sealed record ActiveRun(
    Guid RunId,
    AgentRunState State,
    string? ContainerId,
    bool HasLiveJob,
    string? LastJobError,
    bool CancelRequested,
    TimeSpan? WaitingFor,
    long LogLinesRead,
    long LastEventSeq,
    IReadOnlyList<string> Secrets)
{
    public bool HasOpenBlockingItem => WaitingFor is not null;

    public override string ToString() => $"ActiveRun {{ RunId = {RunId}, State = {State}, ContainerId = {ContainerId}, HasLiveJob = {HasLiveJob} }}";
}
