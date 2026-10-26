namespace Tikai.Core.Events;

/// <summary>
/// Publishes change notifications. Joins the caller's open database transaction, if any,
/// so the event is delivered only when that transaction commits.
/// </summary>
public interface IEventPublisher
{
    /// <param name="topic">Lowercase letters and underscores, e.g. "attention_items".</param>
    Task PublishAsync(string topic, Guid id, CancellationToken cancellationToken = default);
}
