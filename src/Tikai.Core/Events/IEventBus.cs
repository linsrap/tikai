namespace Tikai.Core.Events;

/// <summary>
/// Delivers change notifications to subscribers in this process. Delivery is at most once:
/// after a lost connection subscribers get a resync event (<see cref="BusEvent.IsResync"/>).
/// </summary>
public interface IEventBus
{
    /// <summary>Subscribes to a topic until the returned handle is disposed.</summary>
    IDisposable Subscribe(string topic, Func<BusEvent, CancellationToken, Task> handler);
}
