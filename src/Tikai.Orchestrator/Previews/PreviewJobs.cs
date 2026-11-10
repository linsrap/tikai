using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tikai.Core.Git;
using Tikai.Core.Jobs;

namespace Tikai.Orchestrator.Previews;

/// <summary>
/// Starts a preview (queue <see cref="PreviewJob.StartQueue"/>): bundle the round's pinned ref from the mirror (the only
/// git the hub runs for it), clone, setup steps, services and app (<see cref="PreviewEnvironment"/>), then follow the
/// app's log. Between steps it checks that the preview is still wanted; a stop meanwhile wins. Any failure fails the
/// preview with a reason people can act on and queues its teardown. A start is tried once: after a hub restart the
/// reconciler fails it.
/// </summary>
public sealed partial class PreviewStarter(
    IPreviewStore store,
    PreviewEnvironment environment,
    IGitService git,
    PreviewLogFollower follower,
    ILogger<PreviewStarter> logger) : IJobHandler
{
    public string Queue => PreviewJob.StartQueue;

    public async Task HandleAsync(JobLease job, CancellationToken cancellationToken)
    {
        var previewId = PreviewJob.FromJson(job.Payload).PreviewId;
        var info = await store.GetStartInfoAsync(previewId, cancellationToken);
        if (info is null)
        {
            return;
        }
        Task Log(IReadOnlyList<PreviewLogEntry> lines, CancellationToken ct) => store.AppendLogAsync(previewId, lines, ct);
        try
        {
            await StartAsync(info, Log, cancellationToken);
        }
        catch (Exception ex) when (ex is PreviewStartException or GitException)
        {
            await FailAsync(previewId, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogStartFailed(previewId, ex);
            await FailAsync(previewId, $"Starting the preview failed: {ex.Message}");
        }
    }

    private async Task StartAsync(PreviewStartInfo info, PreviewLogSink log, CancellationToken cancellationToken)
    {
        var previewId = info.PreviewId;
        var definition = PreviewDefinition.Parse(info.Definition) is { Definition: { } parsed } ? parsed
            : throw new PreviewStartException($"{PreviewDefinition.FileName} is not valid any more.");

        var bundle = Path.Combine(Path.GetTempPath(), $"tikai-preview-{previewId:N}.bundle");
        try
        {
            var head = await git.CreateRefBundleAsync(info.MirrorPath, info.RoundRef, bundle, cancellationToken);
            await log([new PreviewLogEntry("hub", $"Preparing round head {head[..12]}")], cancellationToken);
            await environment.WriteSourceAsync(previewId, bundle, cancellationToken);
        }
        finally
        {
            File.Delete(bundle);
        }
        await environment.CloneAsync(previewId, info.RoundRef, log, cancellationToken);
        for (var i = 0; i < definition.Setup.Count; i++)
        {
            if (!await store.IsActiveAsync(previewId, cancellationToken))
            {
                return;
            }
            await environment.RunSetupAsync(previewId, i + 1, definition.Setup[i], log, cancellationToken);
        }
        if (!await store.MarkStartingAsync(previewId, cancellationToken))
        {
            return;
        }
        var started = await environment.StartAsync(previewId, definition, log, cancellationToken);
        if (!await store.MarkRunningAsync(previewId, started.Upstream, started.AppContainerId, cancellationToken))
        {
            // Stopped while it started; its teardown is queued and removes what was just started.
            return;
        }
        await log([new PreviewLogEntry("hub", "Preview running")], cancellationToken);
        LogStarted(previewId);
        follower.Follow(previewId, started);
    }

    private async Task FailAsync(Guid previewId, string reason)
    {
        await store.AppendLogAsync(previewId, [new PreviewLogEntry("hub", "Preview failed: " + reason)], CancellationToken.None);
        await store.FailAsync(previewId, reason, null, CancellationToken.None);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Preview {PreviewId} running")]
    private partial void LogStarted(Guid previewId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Preview {PreviewId}: starting failed")]
    private partial void LogStartFailed(Guid previewId, Exception exception);
}

/// <summary>
/// Tears a preview down (queue <see cref="PreviewJob.TeardownQueue"/>): its containers and network, then, once it has
/// ended, its directory through a cleanup container. Queued with every stop and failure, and by the reconciler for
/// leftovers; a failed teardown is retried by the queue and later by the reconciler.
/// </summary>
public sealed partial class PreviewTeardown(
    IPreviewStore store,
    PreviewEnvironment environment,
    PreviewLogFollower follower,
    ILogger<PreviewTeardown> logger) : IJobHandler
{
    public string Queue => PreviewJob.TeardownQueue;

    public async Task HandleAsync(JobLease job, CancellationToken cancellationToken)
    {
        var previewId = PreviewJob.FromJson(job.Payload).PreviewId;
        if (await store.IsActiveAsync(previewId, cancellationToken))
        {
            return;
        }
        follower.Stop(previewId);
        await environment.StopAsync(previewId, cancellationToken);
        if (await store.NeedsCleanupAsync(previewId, cancellationToken))
        {
            await environment.CleanupAsync(previewId, cancellationToken);
            await store.MarkCleanedAsync(previewId, cancellationToken);
        }
        LogTornDown(previewId);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Preview {PreviewId}: containers, network and directory removed")]
    private partial void LogTornDown(Guid previewId);
}

/// <summary>
/// Follows the log of each running app into its preview log while this hub runs. The generated values are only in
/// memory, so after a restart the log is not followed again (the reconciler still notices an app that stops).
/// </summary>
public sealed partial class PreviewLogFollower(IServiceScopeFactory scopes, PreviewEnvironment environment, ILogger<PreviewLogFollower> logger) : IDisposable
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _follows = new();

    public void Follow(Guid previewId, PreviewStarted started)
    {
        var cancel = new CancellationTokenSource();
        if (!_follows.TryAdd(previewId, cancel))
        {
            cancel.Dispose();
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await environment.FollowAppLogAsync(started.AppContainerId, started.Secrets, async (lines, ct) =>
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<IPreviewStore>().AppendLogAsync(previewId, lines, ct);
                }, cancel.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFollowEnded(previewId, ex);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                if (_follows.TryRemove(new KeyValuePair<Guid, CancellationTokenSource>(previewId, cancel)))
                {
                    cancel.Dispose();
                }
            }
        }, CancellationToken.None);
    }

    public void Stop(Guid previewId)
    {
        if (_follows.TryRemove(previewId, out var cancel))
        {
            cancel.Cancel();
            cancel.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var id in _follows.Keys)
        {
            Stop(id);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Preview {PreviewId}: following the app's log ended")]
    private partial void LogFollowEnded(Guid previewId, Exception exception);
}
