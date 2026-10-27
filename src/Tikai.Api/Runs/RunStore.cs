using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Tikai.Api.Attention;
using Tikai.Api.Llm;
using Tikai.Api.Projects;
using Tikai.Core.Agents;
using Tikai.Core.Attention;
using Tikai.Core.Events;
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

    public Task<bool> MarkRunningAsync(Guid runId, CancellationToken cancellationToken) =>
        MoveAsync(runId, RunTrigger.Start, cancellationToken, startedAt: timeProvider.GetUtcNow());

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
        var cancelRequested = await db.AgentRuns.AnyAsync(r => r.Id == runId && r.CancelRequestedAt != null, cancellationToken);
        var items = db.AttentionItems.Where(i => i.RunId == runId);
        var openBlocking = await items.AnyAsync(i => i.Status == AttentionStatus.Open && AttentionKinds.Blocking.Contains(i.Kind), cancellationToken);
        var submitted = await items.AnyAsync(i => i.Kind == AttentionKind.Review, cancellationToken);
        return new RunEndContext(cancelRequested, openBlocking, submitted);
    }

    public async Task<bool> EndAsync(Guid runId, RunEnd outcome, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var run = await db.AgentRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run is null || RunStateMachine.Next(run.State, outcome.Trigger) is not { } next)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
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
        if (outcome.Trigger == RunTrigger.Fail)
        {
            var projectId = await db.Tickets.Where(t => t.Id == run.TicketId).Select(t => t.ProjectId).SingleAsync(cancellationToken);
            await attention.RecordAsync(new RunScope(run.Id, run.TicketId, projectId), AttentionKind.Failure,
                new FailurePayload(reason ?? "The run failed.", exitCode), cancellationToken);
        }
        await events.PublishAsync(AgentRun.Topic, runId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<WatchedRun>> ListRunningAsync(CancellationToken cancellationToken)
    {
        var running = await db.AgentRuns.AsNoTracking()
            .Where(r => r.State == AgentRunState.Running && r.ContainerId != null)
            .ToListAsync(cancellationToken);
        var watched = new List<WatchedRun>();
        foreach (var run in running)
        {
            // The token is not stored; the redactor's pattern still catches it.
            watched.Add(new WatchedRun(run.Id, run.ContainerId!, run.LogLinesRead, run.LastEventSeq, await SecretsAsync(run, cancellationToken)));
        }
        return watched;
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
}
