using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Tikai.Api.Attention;
using Tikai.Api.Llm;
using Tikai.Api.Projects;
using Tikai.Core.Agents;
using Tikai.Core.Attention;
using Tikai.Core.Events;
using Tikai.Core.Jobs;
using Tikai.Core.Llm;
using Tikai.Core.Projects;
using Tikai.Core.Runs;
using Tikai.Core.Tickets;
using Tikai.Data;
using Tikai.Data.Runs;
using Tikai.Mcp;
using Tikai.Orchestrator;

namespace Tikai.Api.Runs;

/// <summary>The orchestrator's view of the database. State changes are compare-and-set updates along <see cref="RunStateMachine"/>.</summary>
internal sealed class RunStore(
    TikaiDbContext db,
    RunService runs,
    LlmProviderService providers,
    MirrorPaths mirrors,
    AttentionService attention,
    IJobQueue jobs,
    IEventPublisher events,
    TimeProvider timeProvider) : IRunStore
{
    public async Task<PrepareClaim> ClaimForPreparingAsync(Guid runId, CancellationToken cancellationToken)
    {
        if (await MoveAsync(runId, RunTrigger.Prepare, cancellationToken))
        {
            return PrepareClaim.Claimed;
        }
        var state = await db.AgentRuns.Where(r => r.Id == runId).Select(r => (AgentRunState?)r.State).SingleOrDefaultAsync(cancellationToken);
        return state == AgentRunState.Preparing ? PrepareClaim.Interrupted : PrepareClaim.NotQueued;
    }

    public async Task<RunStartInfo> GetStartInfoAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.AgentRuns.AsNoTracking().SingleAsync(r => r.Id == runId, cancellationToken);
        var ticket = await db.Tickets.AsNoTracking().Include(t => t.Criteria).SingleAsync(t => t.Id == run.TicketId, cancellationToken);
        var project = await db.Projects.AsNoTracking().SingleAsync(p => p.Id == ticket.ProjectId, cancellationToken);
        var repository = run.RepositoryId is { } repositoryId
            ? await db.Repositories.AsNoTracking().SingleOrDefaultAsync(r => r.Id == repositoryId, cancellationToken)
            : null;
        if (repository is not { Status: RepositoryStatus.Ready })
        {
            throw new RunStartException("The run's repository is not ready (it is still cloning, failed, or was removed).");
        }
        var provider = run.LlmProviderId is { } providerId
            ? await db.LlmProviders.AsNoTracking().SingleOrDefaultAsync(p => p.Id == providerId, cancellationToken)
            : null;
        if (provider is null)
        {
            throw new RunStartException("The run's LLM provider was deleted. Start a new run with another provider.");
        }

        LlmConnection connection;
        try
        {
            connection = providers.Connection(provider) with { Model = run.Model ?? provider.DefaultModel };
        }
        catch (CryptographicException)
        {
            throw new RunStartException($"The key of \"{provider.Name}\" cannot be decrypted (the hub's keys changed). Enter the key again.");
        }

        var key = TicketKey.Format(project.Key, ticket.Number);
        return new RunStartInfo(
            run.Id,
            key,
            ticket.Title,
            TicketDocument.Render(key, ticket.Title, ticket.Description, ticket.Criteria),
            run.Branch ?? RunBranch.Name(key, run.Id),
            repository.DefaultBranch,
            mirrors.For(project.Id, repository.Id),
            connection);
    }

    public Task<string?> RenewTokenAsync(Guid runId, CancellationToken cancellationToken) => runs.RenewTokenAsync(runId, cancellationToken);

    public async Task SetContainerAsync(Guid runId, string? containerId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await db.AgentRuns.Where(r => r.Id == runId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ContainerId, containerId).SetProperty(r => r.UpdatedAt, now), cancellationToken);
    }

    public async Task AttachContainerAsync(Guid runId, string containerId, RunAttemptKind kind, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await db.AgentRuns.Where(r => r.Id == runId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.ContainerId, containerId)
                .SetProperty(r => r.LogLinesRead, 0)
                .SetProperty(r => r.Attempt, r => r.Attempt + 1)
                .SetProperty(r => r.AttemptKind, kind)
                .SetProperty(r => r.UpdatedAt, now), cancellationToken);
        await events.PublishAsync(AgentRun.Topic, runId, cancellationToken);
    }

    public Task<bool> MarkRunningAsync(Guid runId, CancellationToken cancellationToken) =>
        MoveAsync(runId, RunTrigger.Start, cancellationToken, startedAt: timeProvider.GetUtcNow());

    public async Task<ResumeInfo?> GetResumeInfoAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.AgentRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run is null)
        {
            return null;
        }
        var blocking = await db.AttentionItems.AsNoTracking()
            .Where(i => i.RunId == runId && AttentionKinds.Blocking.Contains(i.Kind))
            .OrderBy(i => i.CreatedAt).ThenBy(i => i.Id)
            .ToListAsync(cancellationToken);
        var anyOpen = blocking.Any(i => i.Status == AttentionStatus.Open);
        var answered = blocking.Where(i => i.Status == AttentionStatus.Answered).Select(i => (i.Id, i.DeliveredAt, Answer: Answered(i))).ToList();
        var toDeliver = answered.Where(a => a.DeliveredAt is null).Select(a => (a.Id, a.Answer)).ToList();
        if (toDeliver.Count == 0 && answered.Count > 0)
        {
            // Nothing new (e.g. the reconciler resumed a run whose answer was already handed out): repeat the latest one.
            toDeliver.Add((answered[^1].Id, answered[^1].Answer));
        }
        var approved = answered.LastOrDefault(a => a.Answer is { Kind: AttentionKind.PlanApproval, Answer.Decision: PlanDecision.Approve }).Answer?.Plan;
        var canResume = run.CancelRequestedAt is null && run.ContainerId is null
            && (run.State == AgentRunState.Blocked && !anyOpen || run.State == AgentRunState.Running);
        return new ResumeInfo(run.State, canResume, run.CancelRequestedAt is not null, run.SessionId, toDeliver, [.. answered.Select(a => a.Answer)], approved);
    }

    public async Task<bool> MarkResumedAsync(Guid runId, string containerId, IReadOnlyList<Guid> deliveredItems, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var run = await RunResumes.LockAsync(db, runId, cancellationToken);
        if (run is null || run.ContainerId != containerId || run.CancelRequestedAt is not null || run.State is not (AgentRunState.Blocked or AgentRunState.Running))
        {
            return false;
        }
        var now = timeProvider.GetUtcNow();
        if (run.State == AgentRunState.Blocked)
        {
            await db.AgentRuns.Where(r => r.Id == runId && r.State == AgentRunState.Blocked)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.State, AgentRunState.Running).SetProperty(r => r.UpdatedAt, now), cancellationToken);
        }
        await db.AttentionItems.Where(i => deliveredItems.Contains(i.Id) && i.RunId == runId && i.DeliveredAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.DeliveredAt, now).SetProperty(i => i.UpdatedAt, now), cancellationToken);
        await events.PublishAsync(AgentRun.Topic, runId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task AppendEventsAsync(Guid runId, IReadOnlyList<ParsedEvent> runEvents, long? logLinesRead, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.AppendAsync(runId, runEvents, logLinesRead, timeProvider.GetUtcNow(), cancellationToken);
        if (runEvents.Count > 0)
        {
            await events.PublishAsync(RunEvent.Topic, runId, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SetSessionAsync(Guid runId, string sessionId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var session = sessionId.Length > 100 ? sessionId[..100] : sessionId;
        await db.AgentRuns.Where(r => r.Id == runId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.SessionId, session).SetProperty(r => r.UpdatedAt, now), cancellationToken);
    }

    public async Task<RunEndContext> GetEndContextAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.AgentRuns.AsNoTracking().Where(r => r.Id == runId)
            .Select(r => new { CancelRequested = r.CancelRequestedAt != null, r.AttemptKind })
            .SingleAsync(cancellationToken);
        var items = db.AttentionItems.Where(i => i.RunId == runId);
        var blocking = items.Where(i => AttentionKinds.Blocking.Contains(i.Kind));
        var openBlocking = await blocking.AnyAsync(i => i.Status == AttentionStatus.Open, cancellationToken);
        var undelivered = await blocking.AnyAsync(i => i.Status == AttentionStatus.Answered && i.DeliveredAt == null, cancellationToken);
        var submitted = await items.AnyAsync(i => i.Kind == AttentionKind.Review, cancellationToken);
        return new RunEndContext(run.CancelRequested, openBlocking, submitted, undelivered, run.AttemptKind);
    }

    public async Task<bool> EndAsync(Guid runId, RunEnd outcome, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var run = await RunResumes.LockAsync(db, runId, cancellationToken);
        // Only the container the run has now may end it (null: a run without one, e.g. a blocked run being cancelled).
        if (run is null || run.ContainerId != outcome.ContainerId || !await EndLockedAsync(run, outcome, cancellationToken))
        {
            return false;
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RetryWithSummaryAsync(Guid runId, string containerId, string reason, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var run = await RunResumes.LockAsync(db, runId, cancellationToken);
        if (run is not { State: AgentRunState.Running } || run.ContainerId != containerId)
        {
            return false;
        }
        var now = timeProvider.GetUtcNow();
        await db.AgentRuns.Where(r => r.Id == runId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ContainerId, (string?)null).SetProperty(r => r.UpdatedAt, now), cancellationToken);
        await db.AppendAsync(runId,
        [
            new ParsedEvent(RunEventKind.Log, new System.Text.Json.Nodes.JsonObject
            {
                ["source"] = "hub",
                ["text"] = "Resume failed; starting a fresh session with a summary",
                ["reason"] = reason,
            }),
        ], null, now, cancellationToken);
        await jobs.EnqueueAsync(RunJob.Queue, new RunJob(runId, RunJobKind.Summary).ToJson(), maxAttempts: 3, cancellationToken: cancellationToken);
        await events.PublishAsync(RunEvent.Topic, runId, cancellationToken);
        await events.PublishAsync(AgentRun.Topic, runId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<ActiveRun>> ListActiveAsync(CancellationToken cancellationToken)
    {
        var active = await db.AgentRuns.AsNoTracking().Where(r => !AgentRunStates.Ended.Contains(r.State)).ToListAsync(cancellationToken);
        if (active.Count == 0)
        {
            return [];
        }
        var jobRows = await db.Database.SqlQuery<RunJobRow>($"""
            SELECT (payload->>'run_id')::uuid AS run_id,
                   bool_or(dead_at IS NULL) AS live,
                   (array_agg(last_error ORDER BY updated_at DESC) FILTER (WHERE dead_at IS NOT NULL))[1] AS last_error
            FROM jobs WHERE queue = {RunJob.Queue}
            GROUP BY 1
            """).ToListAsync(cancellationToken);
        var jobsByRun = jobRows.ToDictionary(j => j.RunId);
        // The database's clock, not the hub's: the block timeout is wall-clock time (spike Q1, clock note).
        var kinds = AttentionKinds.Blocking.Select(k => System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(k.ToString())).ToArray();
        var waits = (await db.Database.SqlQuery<WaitRow>($"""
            SELECT run_id, extract(epoch FROM now() - min(created_at))::float8 AS seconds
            FROM attention_items
            WHERE status = 'open' AND run_id IS NOT NULL AND kind = ANY({kinds})
            GROUP BY run_id
            """).ToListAsync(cancellationToken)).ToDictionary(w => w.RunId, w => TimeSpan.FromSeconds(Math.Max(0, w.Seconds)));

        var list = new List<ActiveRun>();
        foreach (var run in active)
        {
            var job = jobsByRun.GetValueOrDefault(run.Id);
            list.Add(new ActiveRun(
                run.Id,
                run.State,
                run.ContainerId,
                job?.Live ?? false,
                job?.LastError,
                run.CancelRequestedAt is not null,
                waits.TryGetValue(run.Id, out var waiting) ? waiting : null,
                run.LogLinesRead,
                run.LastEventSeq,
                // The token is not stored; the redactor's pattern still catches it.
                run.ContainerId is null ? [] : await SecretsAsync(run, cancellationToken)));
        }
        return list;
    }

    public async Task<bool> FailAbandonedAsync(Guid runId, AgentRunState state, string? containerId, string reason, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var run = await RunResumes.LockAsync(db, runId, cancellationToken);
        if (run is null || run.State != state || run.ContainerId != containerId || await RunResumes.HasLiveJobAsync(db, runId, cancellationToken))
        {
            return false;
        }
        // The containers of an abandoned run are removed by the caller; the run forgets them now.
        if (!await EndLockedAsync(run, new RunEnd(RunTrigger.Fail, reason), cancellationToken))
        {
            return false;
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RequestResumeAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var requested = await RunResumes.RequestIfReadyAsync(db, jobs, runId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return requested;
    }

    public async Task<CancelTarget?> RequestCancelAsync(Guid runId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await db.AgentRuns.Where(r => r.Id == runId && !AgentRunStates.Ended.Contains(r.State) && r.CancelRequestedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.CancelRequestedAt, now).SetProperty(r => r.UpdatedAt, now), cancellationToken);
        return await db.AgentRuns.Where(r => r.Id == runId)
            .Select(r => new CancelTarget(r.State, r.ContainerId))
            .SingleOrDefaultAsync(cancellationToken);
    }

    /// <summary>The body of <see cref="EndAsync"/>, inside its transaction with the run row locked.</summary>
    private async Task<bool> EndLockedAsync(AgentRun run, RunEnd outcome, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var runId = run.Id;

        // The container ended while the run waits: it stays blocked, without a container, until everything is answered.
        if (outcome.Trigger == RunTrigger.Block && run.State == AgentRunState.Blocked)
        {
            int? blockedExit = outcome.ExitCode ?? run.ExitCode;
            decimal? blockedUsd = outcome.Cost?.Usd ?? run.CostUsd;
            long? blockedTokens = outcome.Cost?.Tokens ?? run.CostTokens;
            await db.AgentRuns.Where(r => r.Id == runId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.ContainerId, (string?)null)
                    .SetProperty(r => r.ExitCode, blockedExit)
                    .SetProperty(r => r.CostUsd, blockedUsd)
                    .SetProperty(r => r.CostTokens, blockedTokens)
                    .SetProperty(r => r.UpdatedAt, now), cancellationToken);
            await RunResumes.RequestIfReadyAsync(db, jobs, runId, cancellationToken);
            await events.PublishAsync(AgentRun.Topic, runId, cancellationToken);
            return true;
        }
        if (RunStateMachine.Next(run.State, outcome.Trigger) is not { } next)
        {
            return false;
        }

        var reason = outcome.Reason is { } text && text.Length > RunRules.MaxFailureReasonLength ? text[..RunRules.MaxFailureReasonLength] : outcome.Reason;
        var failureReason = outcome.Trigger == RunTrigger.Fail ? reason : run.FailureReason;
        var exitCode = outcome.ExitCode ?? run.ExitCode;
        var costUsd = outcome.Cost?.Usd ?? run.CostUsd;
        var costTokens = outcome.Cost?.Tokens ?? run.CostTokens;
        DateTimeOffset? endedAt = next.IsEnded() ? now : null;
        var updated = await db.AgentRuns
            .Where(r => r.Id == runId && r.State == run.State)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.State, next)
                .SetProperty(r => r.ContainerId, (string?)null)
                .SetProperty(r => r.FailureReason, failureReason)
                .SetProperty(r => r.ExitCode, exitCode)
                .SetProperty(r => r.CostUsd, costUsd)
                .SetProperty(r => r.CostTokens, costTokens)
                .SetProperty(r => r.EndedAt, endedAt)
                .SetProperty(r => r.UpdatedAt, now), cancellationToken);
        if (updated == 0)
        {
            return false;
        }

        // The ticket follows its run, but a status someone set by hand meanwhile is left alone.
        var (from, to) = outcome.Trigger switch
        {
            RunTrigger.Submit => ([TicketStatus.InProgress], TicketStatus.InReview),
            RunTrigger.Fail or RunTrigger.Cancel => (new[] { TicketStatus.InProgress, TicketStatus.InReview }, TicketStatus.Open),
            _ => (Array.Empty<TicketStatus>(), (TicketStatus?)null),
        };
        if (to is { } status)
        {
            await db.Tickets.Where(t => t.Id == run.TicketId && from.Contains(t.Status))
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, status).SetProperty(t => t.UpdatedAt, now), cancellationToken);
        }
        if (next.IsEnded())
        {
            // No agent will ever get these answers: they leave the open list, and answering one is refused.
            await attention.DismissOpenBlockingAsync(runId, cancellationToken);
        }
        if (outcome.Trigger == RunTrigger.Fail)
        {
            var projectId = await db.Tickets.Where(t => t.Id == run.TicketId).Select(t => t.ProjectId).SingleAsync(cancellationToken);
            await attention.RecordAsync(new RunScope(run.Id, run.TicketId, projectId), AttentionKind.Failure,
                new FailurePayload(reason ?? "The run failed.", exitCode), cancellationToken);
        }
        if (next == AgentRunState.Blocked)
        {
            // Blocked by an answer that came too late for the agent: nothing is open, so a new container brings it.
            await RunResumes.RequestIfReadyAsync(db, jobs, runId, cancellationToken);
        }
        await events.PublishAsync(AgentRun.Topic, runId, cancellationToken);
        return true;
    }

    private static AnsweredRequest Answered(AttentionItem item) => new(
        item.Kind,
        item.Kind == AttentionKind.Question ? AttentionJson.Deserialize<QuestionPayload>(item.Payload) : null,
        item.Kind == AttentionKind.PlanApproval ? AttentionJson.Deserialize<PlanPayload>(item.Payload) : null,
        AttentionService.AnswerOf(item)!);

    private async Task<IReadOnlyList<string>> SecretsAsync(AgentRun run, CancellationToken cancellationToken)
    {
        var provider = run.LlmProviderId is { } id ? await db.LlmProviders.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken) : null;
        try
        {
            return provider is null ? [] : [providers.Connection(provider).ApiKey];
        }
        catch (CryptographicException)
        {
            return [];
        }
    }

    private async Task<bool> MoveAsync(Guid runId, RunTrigger trigger, CancellationToken cancellationToken, DateTimeOffset? startedAt = null)
    {
        var from = RunStateMachine.From(trigger);
        if (from.Count != 1)
        {
            throw new InvalidOperationException($"{trigger} starts from more than one state; use {nameof(EndAsync)}.");
        }
        var next = RunStateMachine.Next(from[0], trigger)!.Value;
        var now = timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var updated = await db.AgentRuns.Where(r => r.Id == runId && r.State == from[0])
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.State, next)
                .SetProperty(r => r.StartedAt, r => startedAt ?? r.StartedAt)
                .SetProperty(r => r.UpdatedAt, now), cancellationToken);
        if (updated == 0)
        {
            return false;
        }
        await events.PublishAsync(AgentRun.Topic, runId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private sealed record RunJobRow(Guid RunId, bool Live, string? LastError);

    private sealed record WaitRow(Guid RunId, double Seconds);
}
