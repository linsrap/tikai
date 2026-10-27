using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tikai.Core.Agents;
using Tikai.Core.Runs;

namespace Tikai.Orchestrator;

/// <summary>A container of this installation (it mounts the runs volume) that carries a run id label.</summary>
public sealed record RunContainer(string Id, Guid RunId, SandboxRole? Role, bool Running);

/// <summary>Something the reconciler does to bring a run and the containers back in line.</summary>
public abstract record ReconcileAction(Guid RunId);

/// <summary>Follow the run's container (again): the monitor reads its log and maps an end that happened meanwhile.</summary>
public sealed record WatchAction(WatchedRun Run) : ReconcileAction(Run.RunId);

/// <summary>Nobody works on the run any more: fail it if it is still in that state with that container.</summary>
public sealed record FailAction(Guid RunId, AgentRunState State, string? ContainerId, string Reason) : ReconcileAction(RunId);

/// <summary>The run's container was not listed: check it once more, then end that container's part of the run.</summary>
public sealed record ContainerGoneAction(Guid RunId, AgentRunState State, string ContainerId) : ReconcileAction(RunId);

/// <summary>Blocked, without a container, nothing open and no job: continue the run.</summary>
public sealed record ResumeAction(Guid RunId) : ReconcileAction(RunId);

/// <summary>The agent waited longer than the block timeout: stop its container (the run stays blocked).</summary>
public sealed record StopForTimeoutAction(Guid RunId, string ContainerId, TimeSpan WaitingFor) : ReconcileAction(RunId);

/// <summary>A container no run claims.</summary>
public sealed record RemoveAction(Guid RunId, string ContainerId) : ReconcileAction(RunId);

/// <summary>
/// Compares the runs that have not ended with the containers on the daemon. Pure, so every case is unit-tested; the
/// <see cref="RunReconciler"/> reads the inputs (containers first, then runs, so no container of a run created
/// in between looks like an orphan) and carries the actions out, each conditional on what was read.
/// </summary>
public static class RunReconciliation
{
    public static IReadOnlyList<ReconcileAction> Plan(
        IReadOnlyList<RunContainer> containers, IReadOnlyList<ActiveRun> runs, IReadOnlySet<Guid> watched, TimeSpan blockTimeout)
    {
        var actions = new List<ReconcileAction>();
        var listed = containers.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var active = runs.ToDictionary(r => r.RunId);

        foreach (var run in runs)
        {
            switch (run)
            {
                case { State: AgentRunState.Queued or AgentRunState.Preparing, HasLiveJob: false }:
                    actions.Add(new FailAction(run.RunId, run.State, run.ContainerId,
                        "The start job gave up" + (run.LastJobError is { } error ? $" ({error})" : "") + ". Start a new run."));
                    break;
                case { State: AgentRunState.Running or AgentRunState.Blocked, ContainerId: { } containerId }:
                    if (!listed.Contains(containerId))
                    {
                        actions.Add(new ContainerGoneAction(run.RunId, run.State, containerId));
                        break;
                    }
                    if (!watched.Contains(run.RunId))
                    {
                        actions.Add(new WatchAction(new WatchedRun(run.RunId, containerId, run.LogLinesRead, run.LastEventSeq, run.Secrets)));
                    }
                    if (run is { State: AgentRunState.Blocked, CancelRequested: false, WaitingFor: { } waiting } && waiting >= blockTimeout
                        && containers.Any(c => c.Id == containerId && c.Running))
                    {
                        actions.Add(new StopForTimeoutAction(run.RunId, containerId, waiting));
                    }
                    break;
                case { State: AgentRunState.Running, ContainerId: null, HasLiveJob: false }:
                    actions.Add(new FailAction(run.RunId, run.State, null, "The hub stopped while resuming this run. Start a new run."));
                    break;
                case { State: AgentRunState.Blocked, ContainerId: null, HasOpenBlockingItem: false, HasLiveJob: false, CancelRequested: false }:
                    actions.Add(new ResumeAction(run.RunId));
                    break;
                default:
                    break;
            }
        }

        foreach (var container in containers)
        {
            var claimed = active.TryGetValue(container.RunId, out var run)
                && (run.ContainerId == container.Id || run.HasLiveJob);
            if (!claimed)
            {
                actions.Add(new RemoveAction(container.RunId, container.Id));
            }
        }
        return actions;
    }
}

/// <summary>
/// Brings runs and containers back in line, at startup and then every <see cref="OrchestratorOptions.ReconcileInterval"/>:
/// follows agent containers nobody follows (after a restart, or after a database error ended a watch), fails runs
/// nobody works on, resumes blocked runs that were answered meanwhile, stops agents that waited past the block
/// timeout, and removes containers no run claims. A pass that fails is simply tried again at the next interval.
/// </summary>
public sealed partial class RunReconciler(
    IServiceScopeFactory scopes,
    IContainerRuntime containers,
    RunMonitor monitor,
    IOptions<OrchestratorOptions> options,
    ILogger<RunReconciler> logger) : BackgroundService
{
    private readonly OrchestratorOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PassAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                LogPassFailed(ex);
            }
            try
            {
                await Task.Delay(_options.ReconcileInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One comparison of runs and containers, and its actions.</summary>
    public async Task PassAsync(CancellationToken cancellationToken)
    {
        // Containers first: a container created after this listing belongs to a run the next read still sees as active.
        var listed = await containers.ListAsync(cancellationToken);
        var runs = await WithStoreAsync(store => store.ListActiveAsync(cancellationToken));
        var actions = RunReconciliation.Plan(listed, runs, monitor.Watched.ToHashSet(), _options.BlockTimeout);
        foreach (var action in actions)
        {
            try
            {
                await ApplyAsync(action, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogActionFailed(action.RunId, action.GetType().Name, ex);
            }
        }
    }

    private async Task ApplyAsync(ReconcileAction action, CancellationToken cancellationToken)
    {
        switch (action)
        {
            case WatchAction watch:
                LogWatching(watch.RunId, watch.Run.ContainerId);
                monitor.Watch(watch.Run);
                break;
            case FailAction fail:
                if (await WithStoreAsync(store => store.FailAbandonedAsync(fail.RunId, fail.State, fail.ContainerId, fail.Reason, cancellationToken)))
                {
                    LogFailed(fail.RunId, fail.Reason);
                    foreach (var role in Enum.GetValues<SandboxRole>())
                    {
                        await containers.RemoveAsync(DockerContainerRuntime.Name(role, fail.RunId), CancellationToken.None);
                    }
                }
                break;
            case ContainerGoneAction gone:
                // The listing is older than the run row: the container may have been created after it.
                if (await containers.InspectAsync(gone.ContainerId, cancellationToken) is not null)
                {
                    break;
                }
                var end = gone.State == AgentRunState.Blocked
                    ? new RunEnd(RunTrigger.Block, null, ContainerId: gone.ContainerId)
                    : new RunEnd(RunTrigger.Fail, "The agent container disappeared.", ContainerId: gone.ContainerId);
                await WithStoreAsync(store => store.EndAsync(gone.RunId, end, cancellationToken));
                break;
            case ResumeAction resume:
                if (await WithStoreAsync(store => store.RequestResumeAsync(resume.RunId, cancellationToken)))
                {
                    LogResumeRequested(resume.RunId);
                }
                break;
            case StopForTimeoutAction stop:
                var waited = stop.WaitingFor.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);
                await WithStoreAsync(store => store.AppendEventsAsync(stop.RunId,
                [
                    new ParsedEvent(RunEventKind.Log, new JsonObject
                    {
                        ["source"] = "hub",
                        ["text"] = "Stopped the agent after the block timeout",
                        ["reason"] = $"It waited {waited} for an answer; it continues when the question is answered.",
                    }),
                ], null, cancellationToken));
                LogTimeout(stop.RunId, stop.WaitingFor);
                await containers.StopAsync(stop.ContainerId, _options.StopTimeout, cancellationToken);
                break;
            case RemoveAction remove:
                LogRemoving(remove.RunId, remove.ContainerId);
                await containers.RemoveAsync(remove.ContainerId, CancellationToken.None);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown action.");
        }
    }

    private async Task<T> WithStoreAsync<T>(Func<IRunStore, Task<T>> action)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<IRunStore>());
    }

    private async Task WithStoreAsync(Func<IRunStore, Task> action)
    {
        await using var scope = scopes.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<IRunStore>());
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconciling runs and containers failed; trying again at the next interval")]
    private partial void LogPassFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId}: {Action} failed")]
    private partial void LogActionFailed(Guid runId, string action, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId}: following agent container {ContainerId} again")]
    private partial void LogWatching(Guid runId, string containerId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId}: failed by the reconciler: {Reason}")]
    private partial void LogFailed(Guid runId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId}: answered while no agent waited; resuming")]
    private partial void LogResumeRequested(Guid runId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId}: stopping the agent after waiting {WaitingFor} for an answer")]
    private partial void LogTimeout(Guid runId, TimeSpan waitingFor);

    [LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId}: removing container {ContainerId}, which no run claims")]
    private partial void LogRemoving(Guid runId, string containerId);
}
