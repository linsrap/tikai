using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tikai.Core.Previews;

namespace Tikai.Orchestrator.Previews;

/// <summary>Something the reconciler does to bring previews and their containers back in line.</summary>
public abstract record PreviewReconcileAction(Guid PreviewId);

/// <summary>Stop the preview (idle, time limit, change set ended); its teardown is queued with it.</summary>
public sealed record StopPreviewAction(Guid PreviewId, string Reason) : PreviewReconcileAction(PreviewId);

/// <summary>Fail the preview if it is still in that state (nobody starts it any more, or its app stopped).</summary>
public sealed record FailPreviewAction(Guid PreviewId, PreviewState State, string Reason) : PreviewReconcileAction(PreviewId);

/// <summary>Queue a teardown: containers or a network of an ended preview, or its directory, are still there.</summary>
public sealed record TeardownPreviewAction(Guid PreviewId) : PreviewReconcileAction(PreviewId);

/// <summary>Compares the active previews with the containers and networks on the daemon. Pure, so every case is unit-tested.</summary>
public static class PreviewReconciliation
{
    public const string ChangeSetEnded = "The change set was merged, pushed or closed.";

    public static IReadOnlyList<PreviewReconcileAction> Plan(
        IReadOnlyList<PreviewResource> resources, IReadOnlyList<ActivePreview> previews, IReadOnlyList<Guid> uncleaned, TimeSpan maxLifetime)
    {
        var actions = new List<PreviewReconcileAction>();
        foreach (var preview in previews)
        {
            if (!preview.ChangeSetOpen)
            {
                actions.Add(new StopPreviewAction(preview.PreviewId, ChangeSetEnded));
                continue;
            }
            if (preview.State != PreviewState.Running)
            {
                if (!preview.HasLiveStartJob)
                {
                    actions.Add(new FailPreviewAction(preview.PreviewId, preview.State, "The hub stopped while starting this preview. Start it again."));
                }
                continue;
            }
            if (!resources.Any(r => !r.IsNetwork && r.Id == preview.AppContainerId && r.Running))
            {
                actions.Add(new FailPreviewAction(preview.PreviewId, preview.State, "The app stopped."));
            }
            else if (preview.Age >= maxLifetime)
            {
                actions.Add(new StopPreviewAction(preview.PreviewId,
                    $"It ran for the limit of {maxLifetime.TotalHours.ToString("0.#", CultureInfo.InvariantCulture)} hours."));
            }
            else if (preview.IdleFor >= TimeSpan.FromMinutes(preview.IdleMinutes))
            {
                actions.Add(new StopPreviewAction(preview.PreviewId, $"Nobody used it for {preview.IdleMinutes} minutes."));
            }
        }

        // A start still at work owns its containers; a cleanup container belongs to its teardown.
        var active = previews.Select(p => p.PreviewId).ToHashSet();
        var teardowns = resources
            .Where(r => r.Role != PreviewRole.Cleanup && !active.Contains(r.PreviewId))
            .Select(r => r.PreviewId)
            .Concat(uncleaned)
            .Distinct();
        actions.AddRange(teardowns.Select(id => new TeardownPreviewAction(id)));
        return actions;
    }
}

/// <summary>
/// Brings previews and containers back in line every <see cref="OrchestratorOptions.ReconcileInterval"/>: stops idle
/// previews, previews past the time limit and previews of ended change sets, fails starts nobody works on and apps
/// that stopped, and queues the teardown of whatever ended previews left behind.
/// </summary>
public sealed partial class PreviewReconciler(
    IServiceScopeFactory scopes,
    IContainerRuntime containers,
    IOptions<OrchestratorOptions> agentOptions,
    IOptions<PreviewOptions> options,
    IOptions<DockerOptions> docker,
    ILogger<PreviewReconciler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The run reconciler says so.
        if (!docker.Value.Enabled)
        {
            return;
        }
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
                await Task.Delay(agentOptions.Value.ReconcileInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task PassAsync(CancellationToken cancellationToken)
    {
        // Containers first: one created after this listing belongs to a preview the next read still sees as active.
        var resources = await containers.ListPreviewsAsync(cancellationToken);
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IPreviewStore>();
        var previews = await store.ListActiveAsync(cancellationToken);
        var uncleaned = await store.ListUncleanedAsync(cancellationToken);
        foreach (var action in PreviewReconciliation.Plan(resources, previews, uncleaned, options.Value.MaxLifetime))
        {
            try
            {
                switch (action)
                {
                    case StopPreviewAction stop when await store.StopAsync(stop.PreviewId, stop.Reason, cancellationToken):
                        LogStopped(stop.PreviewId, stop.Reason);
                        break;
                    case FailPreviewAction fail when await store.FailAsync(fail.PreviewId, fail.Reason, fail.State, cancellationToken):
                        LogFailed(fail.PreviewId, fail.Reason);
                        break;
                    case TeardownPreviewAction teardown:
                        await store.RequestTeardownAsync(teardown.PreviewId, cancellationToken);
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogActionFailed(action.PreviewId, action.GetType().Name, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconciling previews failed; trying again at the next interval")]
    private partial void LogPassFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Preview {PreviewId}: {Action} failed")]
    private partial void LogActionFailed(Guid previewId, string action, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Preview {PreviewId} stopped: {Reason}")]
    private partial void LogStopped(Guid previewId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Preview {PreviewId} failed: {Reason}")]
    private partial void LogFailed(Guid previewId, string reason);
}
