using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Tikai.Core.Attention;
using Tikai.Core.Events;
using Tikai.Core.Reviews;
using Tikai.Core.Runs;
using Tikai.Data;

namespace Tikai.Api.Live;

/// <summary>
/// Turns database change events into hub messages: run and item changes to everyone watching all projects or the
/// project, new transcript events to those watching the run. A resync event (the listener reconnected, notifications
/// may be lost) tells every client to re-read.
/// </summary>
internal sealed partial class LiveUpdates(IEventBus bus, IHubContext<LiveHub, ILiveClient> hub, IServiceScopeFactory scopes, ILogger<LiveUpdates> logger)
    : IHostedService
{
    private IDisposable[] _subscriptions = [];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscriptions =
        [
            bus.Subscribe(AgentRun.Topic, OnRunAsync),
            bus.Subscribe(AttentionItem.Topic, OnItemAsync),
            bus.Subscribe(RunEvent.Topic, OnRunEventsAsync),
            bus.Subscribe(ChangeSet.Topic, OnChangeSetAsync),
        ];
        return Task.CompletedTask;
    }

    // The host may stop services more than once (and concurrently, when a test host is disposed while stopping).
    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var subscription in Interlocked.Exchange(ref _subscriptions, []))
        {
            subscription.Dispose();
        }
        return Task.CompletedTask;
    }

    private async Task OnRunAsync(BusEvent e, CancellationToken cancellationToken)
    {
        if (e.Id is not { } runId)
        {
            await ResyncAsync();
            return;
        }
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TikaiDbContext>();
        var target = await (
            from r in db.AgentRuns
            join t in db.Tickets on r.TicketId equals t.Id
            join p in db.Projects on t.ProjectId equals p.Id
            where r.Id == runId
            select new { ProjectId = p.Id, p.Slug, t.Number }).SingleOrDefaultAsync(cancellationToken);
        if (target is not null)
        {
            await hub.Clients.Groups(LiveGroups.All, LiveGroups.Project(target.ProjectId)).RunChanged(new(runId, target.Slug, target.Number));
        }
    }

    private async Task OnItemAsync(BusEvent e, CancellationToken cancellationToken)
    {
        if (e.Id is not { } itemId)
        {
            await ResyncAsync();
            return;
        }
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TikaiDbContext>();
        var target = await (
            from i in db.AttentionItems
            join t in db.Tickets on i.TicketId equals t.Id
            join p in db.Projects on t.ProjectId equals p.Id
            where i.Id == itemId
            select new { ProjectId = p.Id, p.Slug, t.Number }).SingleOrDefaultAsync(cancellationToken);
        if (target is not null)
        {
            await hub.Clients.Groups(LiveGroups.All, LiveGroups.Project(target.ProjectId)).AttentionChanged(new(itemId, target.Slug, target.Number));
        }
    }

    private async Task OnChangeSetAsync(BusEvent e, CancellationToken cancellationToken)
    {
        if (e.Id is not { } changeSetId)
        {
            await ResyncAsync();
            return;
        }
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TikaiDbContext>();
        var target = await (
            from c in db.ChangeSets
            join t in db.Tickets on c.TicketId equals t.Id
            join p in db.Projects on t.ProjectId equals p.Id
            where c.Id == changeSetId
            select new { ProjectId = p.Id, p.Slug, t.Number }).SingleOrDefaultAsync(cancellationToken);
        if (target is not null)
        {
            await hub.Clients.Groups(LiveGroups.All, LiveGroups.Project(target.ProjectId)).ChangeSetChanged(new(changeSetId, target.Slug, target.Number));
        }
    }

    // The payload of a run_events notification is the run's id (one per batch of events).
    private async Task OnRunEventsAsync(BusEvent e, CancellationToken cancellationToken)
    {
        if (e.Id is not { } runId)
        {
            await ResyncAsync();
            return;
        }
        await hub.Clients.Group(LiveGroups.Run(runId)).RunEvents(new(runId));
    }

    /// <summary>The bus sends one resync per topic after a reconnect, so clients re-read a few times; that is cheap and rare.</summary>
    private async Task ResyncAsync()
    {
        LogResync();
        await hub.Clients.All.Resync();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Event listener reconnected; asking browsers to re-read")]
    private partial void LogResync();
}
