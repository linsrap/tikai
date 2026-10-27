using System.Text.Json;
using Tikai.Core.Attention;
using Tikai.Core.Runs;

namespace Tikai.Api.Runs;

/// <param name="RepositoryId">Needed when the project has more than one repository.</param>
/// <param name="ProviderId">Whose API key the agent uses; by default the project's provider, else the person's own.</param>
public sealed record StartRunRequest(Guid? RepositoryId = null, Guid? ProviderId = null);

/// <param name="TicketKey">e.g. "HUB-42".</param>
/// <param name="ProjectSlug">For links to the ticket.</param>
/// <param name="ProgressStep">What the agent last reported with report_progress.</param>
/// <param name="CostUsd">As the agent reported it at its end.</param>
/// <param name="Attempt">Agent containers so far: 1 for the first, one more for each resume.</param>
/// <param name="AttemptKind">How the current (or last) container took up the run: start, resume (its session), summary (a fresh session).</param>
/// <param name="Waiting">The open questions and plans the agent waits for, oldest first.</param>
/// <param name="BlockedSince">When the oldest of them was asked.</param>
/// <param name="BlockTimeoutAt">When the waiting container will be stopped (it continues in a new one once answered).</param>
/// <param name="AgentStopped">Blocked and no container: the agent stopped waiting, or was stopped at the block timeout.</param>
public sealed record RunInfo(
    Guid Id,
    string TicketKey,
    int TicketNumber,
    string ProjectSlug,
    string TicketTitle,
    AgentRunState State,
    string? Adapter,
    string? Model,
    Guid? RepositoryId,
    string? RepositoryName,
    Guid? LlmProviderId,
    string? Branch,
    string? ProgressStep,
    string? ProgressNote,
    DateTimeOffset? ProgressAt,
    int? ExitCode,
    string? FailureReason,
    decimal? CostUsd,
    long? CostTokens,
    bool CancelRequested,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    int Attempt,
    RunAttemptKind AttemptKind,
    IReadOnlyList<WaitingItem> Waiting,
    DateTimeOffset? BlockedSince,
    DateTimeOffset? BlockTimeoutAt,
    bool AgentStopped);

/// <summary>An open question or plan of a run; answered through <c>POST /api/attention-items/{id}/answer</c>.</summary>
public sealed record WaitingItem(Guid Id, AttentionKind Kind, QuestionPayload? Question, PlanPayload? Plan, DateTimeOffset CreatedAt);

/// <param name="Seq">1, 2, 3… per run; ask for events after the last one seen.</param>
/// <param name="Payload">A JSON object; its fields depend on the kind.</param>
public sealed record RunEventInfo(long Seq, RunEventKind Kind, JsonElement Payload, DateTimeOffset CreatedAt);
