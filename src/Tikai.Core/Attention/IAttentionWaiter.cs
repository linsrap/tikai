namespace Tikai.Core.Attention;

/// <summary>
/// Waits until a human answers an attention item. Completes at once when the item is already answered,
/// so it does not matter whether the answer came before or after the wait started.
/// </summary>
public interface IAttentionWaiter
{
    /// <summary>Waits for the item's answer until cancelled (the caller went away).</summary>
    Task<AttentionAnswer> WaitAsync(Guid itemId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the item again and completes its waits if it was answered. A safety net for a missed notification;
    /// the blocking tools call it on every heartbeat.
    /// </summary>
    Task RecheckAsync(Guid itemId, CancellationToken cancellationToken);
}
