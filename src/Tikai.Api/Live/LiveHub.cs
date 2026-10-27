using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Tikai.Data;

namespace Tikai.Api.Live;

/// <summary>
/// Live updates for the browser. Messages carry ids and keys only: the client re-reads what changed through the REST
/// API, so every view is shaped and checked in one place, and a lost or repeated message costs one more read.
/// Clients choose what they hear by watching groups; membership is per connection, so a reconnected client watches again.
/// </summary>
/// <remarks>
/// Login cookie only (see <see cref="LiveSetup"/>): a run token is no scheme here. Nothing here changes data; actions
/// stay REST calls with antiforgery.
/// </remarks>
internal sealed class LiveHub(TikaiDbContext db) : Hub<ILiveClient>
{
    // Phase 1 has no per-project permissions: every logged-in user may watch everything. They go into these methods.

    /// <summary>Run and item changes of every project (inbox, board).</summary>
    public Task WatchAll() => Groups.AddToGroupAsync(Context.ConnectionId, LiveGroups.All);

    /// <summary>Run and item changes of one project (a ticket page). False for an unknown project.</summary>
    public async Task<bool> WatchProject(string slug)
    {
        var id = await db.Projects.Where(p => p.Slug == slug).Select(p => (Guid?)p.Id).SingleOrDefaultAsync(Context.ConnectionAborted);
        if (id is null)
        {
            return false;
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, LiveGroups.Project(id.Value));
        return true;
    }

    /// <summary>New transcript events of one run (an open live log). False for an unknown run.</summary>
    public async Task<bool> WatchRun(Guid runId)
    {
        if (!await db.AgentRuns.AnyAsync(r => r.Id == runId, Context.ConnectionAborted))
        {
            return false;
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, LiveGroups.Run(runId));
        return true;
    }

    public Task UnwatchRun(Guid runId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, LiveGroups.Run(runId));
}

/// <summary>What the hub sends. The browser re-reads the named run, item or events.</summary>
/// <remarks>Public: SignalR implements typed clients in a dynamic assembly, which cannot see internal types.</remarks>
public interface ILiveClient
{
    Task RunChanged(RunChange change);

    Task AttentionChanged(AttentionChange change);

    /// <summary>A change set got a new round or a verdict, or closed.</summary>
    Task ChangeSetChanged(ChangeSetChange change);

    /// <summary>The run has new transcript events: fetch those after the last seq seen.</summary>
    Task RunEvents(RunEventsChange change);

    /// <summary>Changes may have been missed (the hub lost its database listener): re-read everything shown.</summary>
    Task Resync();
}

public sealed record RunChange(Guid RunId, string ProjectSlug, int TicketNumber);

public sealed record AttentionChange(Guid ItemId, string ProjectSlug, int TicketNumber);

public sealed record ChangeSetChange(Guid ChangeSetId, string ProjectSlug, int TicketNumber);

public sealed record RunEventsChange(Guid RunId);

internal static class LiveGroups
{
    public const string All = "all";

    public static string Project(Guid id) => $"project:{id}";

    public static string Run(Guid id) => $"run:{id}";
}
