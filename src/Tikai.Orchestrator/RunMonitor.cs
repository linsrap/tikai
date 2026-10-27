using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Docker.DotNet;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tikai.Core.Agents;
using Tikai.Core.Runs;

namespace Tikai.Orchestrator;

/// <summary>
/// Follows every running agent container: its output becomes run events, its start is checked (the Tikai MCP server
/// must be connected), and its end becomes a run state. Agents keep running when the hub stops; at startup the
/// monitor picks up every running run again and skips the log lines it already stored.
/// </summary>
public sealed partial class RunMonitor(
    IServiceScopeFactory scopes,
    IContainerRuntime containers,
    IAgentAdapter adapter,
    IOptions<OrchestratorOptions> options,
    ILogger<RunMonitor> logger) : BackgroundService
{
    private readonly OrchestratorOptions _options = options.Value;
    private readonly ConcurrentDictionary<Guid, Task> _watches = new();
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>Runs being followed right now (for tests and diagnostics).</summary>
    public IReadOnlyCollection<Guid> Watched => [.. _watches.Keys];

    /// <summary>Follows the run's container until it ends; a second call for the same run does nothing.</summary>
    public void Watch(WatchedRun run)
    {
        _watches.GetOrAdd(run.RunId, id => Task.Run(async () =>
        {
            try
            {
                await FollowAsync(run, _stopping.Token);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                // The hub stops; the container keeps running and is picked up again at the next start.
            }
            catch (Exception ex)
            {
                LogWatchFailed(run.RunId, ex);
            }
            finally
            {
                _watches.TryRemove(run.RunId, out _);
            }
        }));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IReadOnlyList<WatchedRun> running;
        try
        {
            running = await WithStoreAsync(store => store.ListRunningAsync(stoppingToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogResumeFailed(ex);
            return;
        }
        foreach (var run in running)
        {
            LogResuming(run.RunId, run.ContainerId);
            Watch(run);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _stopping.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // The host can be disposed before it is stopped; Dispose has cancelled the watches already.
        }
        await Task.WhenAll(_watches.Values).WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        try
        {
            _stopping.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        _stopping.Dispose();
        base.Dispose();
    }

    private async Task FollowAsync(WatchedRun run, CancellationToken cancellationToken)
    {
        var redactor = new SecretRedactor(run.Secrets);
        var state = new FollowState(run.LastEventSeq);
        var skip = run.LogLinesRead;
        using var initWatch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = WatchInitAsync(run, state, initWatch.Token);

        while (true)
        {
            // Every (re)read starts at the beginning of the log: the parser sees every line again, events are only
            // stored for lines past the ones already stored.
            var parser = adapter.CreateOutputParser();
            state.Parser = parser;
            long line = 0;
            try
            {
                await foreach (var logLine in containers.ReadLogAsync(run.ContainerId, follow: true, cancellationToken))
                {
                    line++;
                    var events = Parse(parser, redactor, logLine);
                    if (line <= skip)
                    {
                        continue;
                    }
                    await WithStoreAsync(store => store.AppendEventsAsync(run.RunId, state.Limit(events, _options.MaxEventsPerRun), line, cancellationToken));
                    skip = line;
                    await CheckStartAsync(run, state, cancellationToken);
                }
                await CheckStartAsync(run, state, cancellationToken);
                break;
            }
            catch (Exception ex) when (ex is DockerApiException or IOException or HttpRequestException && !cancellationToken.IsCancellationRequested)
            {
                if (await containers.InspectAsync(run.ContainerId, cancellationToken) is null)
                {
                    await EndAsync(run, new RunEnd(RunTrigger.Fail, "The agent container disappeared."), cancellationToken);
                    return;
                }
                LogReconnecting(run.RunId, ex.Message);
                await Task.Delay(_options.ReconnectDelay, cancellationToken);
            }
        }
        await initWatch.CancelAsync();

        var exitCode = await containers.WaitAsync(run.ContainerId, cancellationToken);
        var status = await containers.InspectAsync(run.ContainerId, cancellationToken);
        var context = await WithStoreAsync(store => store.GetEndContextAsync(run.RunId, cancellationToken));
        var decision = RunExit.Map(new ExitFacts(
            context.CancelRequested,
            state.HubStopReason,
            status?.OomKilled ?? false,
            exitCode,
            state.Parser?.Result,
            context.HasOpenBlockingItem,
            context.Submitted));
        await WithStoreAsync(store => store.AppendEventsAsync(run.RunId,
        [
            new ParsedEvent(RunEventKind.Log, new JsonObject
            {
                ["source"] = "hub",
                ["text"] = "Agent exited",
                ["exit_code"] = exitCode,
                ["oom_killed"] = status?.OomKilled ?? false,
                ["outcome"] = decision.Trigger.ToString(),
                ["reason"] = decision.Reason,
            }),
        ], null, cancellationToken));
        await EndAsync(run, new RunEnd(decision.Trigger, decision.Reason, (int)exitCode, state.Parser?.Cost), cancellationToken);
        LogEnded(run.RunId, exitCode, decision.Trigger);
    }

    private static IReadOnlyList<ParsedEvent> Parse(IAgentOutputParser parser, SecretRedactor redactor, LogLine line)
    {
        if (line.SkippedBytes is { } skipped)
        {
            return [new ParsedEvent(RunEventKind.Log, new JsonObject
            {
                ["source"] = "hub",
                ["stream"] = line.Stream.ToString().ToLowerInvariant(),
                ["text"] = $"A line of {skipped} bytes was too long to store.",
            })];
        }
        var text = redactor.Redact(line.Text);
        var events = line.Stream == LogStream.Stdout
            ? parser.ParseLine(text)
            : [new ParsedEvent(RunEventKind.Log, new JsonObject { ["source"] = "agent", ["stream"] = "stderr", ["text"] = text })];
        return [.. events.Select(e => e with { Payload = RunEventPayload.Trim(e.Payload) })];
    }

    /// <summary>Claude Code keeps working (and exits 0) when its MCP server failed, so a failed server stops the run at once.</summary>
    private async Task CheckStartAsync(WatchedRun run, FollowState state, CancellationToken cancellationToken)
    {
        if (state.StartHandled || state.Parser?.Start is not { } start)
        {
            return;
        }
        state.StartHandled = true;
        if (start.SessionId is { } session)
        {
            await WithStoreAsync(store => store.SetSessionAsync(run.RunId, session, cancellationToken));
        }
        if (!start.ToolsConnected)
        {
            state.HubStopReason = start.Problem;
            await containers.StopAsync(run.ContainerId, TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private async Task WatchInitAsync(WatchedRun run, FollowState state, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_options.InitTimeout, cancellationToken);
            if (state.Parser?.Start is null)
            {
                state.HubStopReason = $"The agent did not start within {_options.InitTimeout}.";
                await containers.StopAsync(run.ContainerId, TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task EndAsync(WatchedRun run, RunEnd end, CancellationToken cancellationToken)
    {
        await WithStoreAsync(store => store.EndAsync(run.RunId, end, cancellationToken));
        await containers.RemoveAsync(run.ContainerId, CancellationToken.None);
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

    private sealed class FollowState(long storedEvents)
    {
        private long _stored = storedEvents;
        private bool _limitNoted;

        public IAgentOutputParser? Parser { get; set; }

        public bool StartHandled { get; set; }

        public string? HubStopReason { get; set; }

        /// <summary>Past the limit only results and the hub's notes are kept, plus one note that the limit was reached.</summary>
        public List<ParsedEvent> Limit(IReadOnlyList<ParsedEvent> events, int max)
        {
            var kept = new List<ParsedEvent>();
            foreach (var e in events)
            {
                if (_stored < max || e.Kind == RunEventKind.Result)
                {
                    kept.Add(e);
                    _stored++;
                }
                else if (!_limitNoted)
                {
                    _limitNoted = true;
                    kept.Add(new ParsedEvent(RunEventKind.Log, new JsonObject
                    {
                        ["source"] = "hub",
                        ["text"] = $"Event limit of {max} reached; further output of the agent is not stored.",
                    }));
                }
            }
            return kept;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId}: following agent container {ContainerId} again")]
    private partial void LogResuming(Guid runId, string containerId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId}: agent exited with {ExitCode} → {Trigger}")]
    private partial void LogEnded(Guid runId, long exitCode, RunTrigger trigger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId}: lost the agent's log stream ({Message}); reading it again")]
    private partial void LogReconnecting(Guid runId, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "Run {RunId}: following the agent failed")]
    private partial void LogWatchFailed(Guid runId, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not list running agent runs at startup")]
    private partial void LogResumeFailed(Exception exception);
}
