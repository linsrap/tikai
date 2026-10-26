using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Tikai.Core.Events;

namespace Tikai.Data.Events;

/// <summary>
/// Listens on one dedicated connection (LISTEN per subscribed topic) and dispatches notifications to subscribers.
/// After a lost connection it reconnects with backoff and sends each subscriber a resync event,
/// because notifications sent while disconnected are gone.
/// </summary>
internal sealed partial class PostgresEventBus(
    NpgsqlDataSource dataSource,
    TimeProvider timeProvider,
    ILogger<PostgresEventBus> logger) : BackgroundService, IEventBus
{
    private static readonly TimeSpan _maxReconnectDelay = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Func<BusEvent, CancellationToken, Task>>> _subscribers = new();
    private readonly Channel<BusEvent> _inbox = Channel.CreateUnbounded<BusEvent>(new() { SingleReader = true });
    private CancellationTokenSource? _wake;

    /// <summary>Completes once the listener is connected and listens on every topic subscribed so far.</summary>
    internal TaskCompletionSource Listening { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IDisposable Subscribe(string topic, Func<BusEvent, CancellationToken, Task> handler)
    {
        Topic.Validate(topic);
        var key = Guid.NewGuid();
        var handlers = _subscribers.GetOrAdd(topic, _ => new());
        handlers[key] = handler;
        Listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WakeListener(); // let the listen loop issue LISTEN for a new topic
        return new Subscription(() => handlers.TryRemove(key, out _));
    }

    private void WakeListener()
    {
        try
        {
            _wake?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The loop is between iterations; it picks up the new topic on its next pass.
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(ListenAsync(stoppingToken), DispatchAsync(stoppingToken));

    private async Task ListenAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        var connectedBefore = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync(stoppingToken);
                connection.Notification += OnNotification;
                var listened = new HashSet<string>(StringComparer.Ordinal);

                if (connectedBefore)
                {
                    LogReconnected();
                    foreach (var topic in _subscribers.Keys)
                    {
                        _inbox.Writer.TryWrite(new BusEvent(topic, null));
                    }
                }
                connectedBefore = true;
                failures = 0;

                while (!stoppingToken.IsCancellationRequested)
                {
                    using var wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    _wake = wake;
                    var listening = Listening;
                    foreach (var topic in _subscribers.Keys.Where(listened.Add))
                    {
                        // Topic names are validated ([a-z_]+), so quoting them as identifiers is safe.
                        await using var listen = new NpgsqlCommand($"LISTEN \"{topic}\"", connection);
                        await listen.ExecuteNonQueryAsync(stoppingToken);
                    }
                    listening.TrySetResult();

                    try
                    {
                        await connection.WaitAsync(wake.Token);
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        // Woken up for a new subscription.
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                failures++;
                var delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, failures - 1), _maxReconnectDelay.TotalSeconds));
                LogConnectionLost(delay, ex);
                await Task.Delay(delay, timeProvider, stoppingToken);
            }
        }
    }

    private void OnNotification(object sender, NpgsqlNotificationEventArgs e)
    {
        if (Guid.TryParse(e.Payload, out var id))
        {
            _inbox.Writer.TryWrite(new BusEvent(e.Channel, id));
        }
        else
        {
            LogBadPayload(e.Channel, e.Payload);
        }
    }

    private async Task DispatchAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var busEvent in _inbox.Reader.ReadAllAsync(stoppingToken))
            {
                if (!_subscribers.TryGetValue(busEvent.Topic, out var handlers))
                {
                    continue;
                }
                foreach (var handler in handlers.Values)
                {
                    try
                    {
                        await handler(busEvent, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        LogHandlerFailed(busEvent.Topic, ex);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        public void Dispose() => unsubscribe();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Event listener lost its connection; reconnecting in {Delay}")]
    private partial void LogConnectionLost(TimeSpan delay, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Event listener reconnected; subscribers get a resync event")]
    private partial void LogReconnected();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignoring notification on {Channel} with a payload that is not an id: {Payload}")]
    private partial void LogBadPayload(string channel, string payload);

    [LoggerMessage(Level = LogLevel.Error, Message = "Event handler for topic {Topic} failed")]
    private partial void LogHandlerFailed(string topic, Exception exception);
}
