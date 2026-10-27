using System.Text.Json;
using Tikai.Core.Runs;

namespace Tikai.Api.Runs;

/// <param name="RepositoryId">Needed when the project has more than one repository.</param>
/// <param name="ProviderId">Whose API key the agent uses; by default the project's provider, else the person's own.</param>
public sealed record StartRunRequest(Guid? RepositoryId = null, Guid? ProviderId = null);

/// <param name="TicketKey">e.g. "HUB-42".</param>
/// <param name="ProgressStep">What the agent last reported with report_progress.</param>
/// <param name="CostUsd">As the agent reported it at its end.</param>
public sealed record RunInfo(
    Guid Id,
    string TicketKey,
    int TicketNumber,
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
    DateTimeOffset? EndedAt);

/// <param name="Seq">1, 2, 3… per run; ask for events after the last one seen.</param>
/// <param name="Payload">A JSON object; its fields depend on the kind.</param>
public sealed record RunEventInfo(long Seq, RunEventKind Kind, JsonElement Payload, DateTimeOffset CreatedAt);
