using System.Collections.Concurrent;
using Tikai.Core.Events;
using Tikai.Core.Previews;

namespace Tikai.Api.Previews;

/// <summary>
/// The proxy's short memory of where each preview's apps are, so not every request reads the database. A change of the
/// preview (stop, failure, new log lines; all publish <see cref="Preview.Topic"/>) drops its entry at once, a resync
/// drops all, so a stopped preview is not forwarded to its removed containers for the rest of the entry's lifetime.
/// </summary>
internal sealed class PreviewUpstreamCache(IServiceProvider services) : IHostedService
{
    /// <summary>How long an entry is used without a change event (a lost event costs at most this long).</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<Guid, (IReadOnlyList<PreviewAppUpstream> Apps, DateTimeOffset Until)> _entries = new();
    private IDisposable? _subscription;

    public bool TryGet(Guid previewId, DateTimeOffset now, out IReadOnlyList<PreviewAppUpstream> apps)
    {
        if (_entries.TryGetValue(previewId, out var entry) && entry.Until > now)
        {
            apps = entry.Apps;
            return true;
        }
        apps = [];
        return false;
    }

    public void Set(Guid previewId, IReadOnlyList<PreviewAppUpstream> apps, DateTimeOffset now) => _entries[previewId] = (apps, now + Lifetime);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Resolved here, not in the constructor: the proxy is built without a database too (OpenAPI generation at build time).
        _subscription = services.GetRequiredService<IEventBus>().Subscribe(Preview.Topic, (e, _) =>
        {
            if (e.Id is { } id)
            {
                _entries.TryRemove(id, out var _);
            }
            else
            {
                _entries.Clear();
            }
            return Task.CompletedTask;
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _subscription, null)?.Dispose();
        return Task.CompletedTask;
    }
}
