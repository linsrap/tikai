using Microsoft.EntityFrameworkCore;
using Tikai.Core.Attention;
using Tikai.Core.Events;
using Tikai.Data;

namespace Tikai.Api.Attention;

/// <summary>
/// Lets blocking MCP calls wait for an answer in this process. The database is the source of truth: a wait first
/// registers, then reads the item, so an answer stored before the wait started is found by the read and one stored
/// later wakes it through the <c>attention_items</c> event. After the event listener reconnected (events may be lost)
/// every waited item is read again; the tools' heartbeat re-reads too. Nothing here must survive a restart.
/// </summary>
internal sealed partial class AttentionWaiter(IEventBus bus, IServiceScopeFactory scopes, ILogger<AttentionWaiter> logger)
    : IAttentionWaiter, IHostedService
{
    private readonly Dictionary<Guid, List<TaskCompletionSource<AttentionAnswer>>> _waiting = [];
    private readonly Lock _lock = new();
    private IDisposable? _subscription;

    /// <summary>How many calls wait right now (all items).</summary>
    internal int WaitingCount
    {
        get
        {
            lock (_lock)
            {
                return _waiting.Values.Sum(w => w.Count);
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = bus.Subscribe(AttentionItem.Topic, OnEventAsync);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }

    public async Task<AttentionAnswer> WaitAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var waiter = new TaskCompletionSource<AttentionAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            if (!_waiting.TryGetValue(itemId, out var list))
            {
                _waiting[itemId] = list = [];
            }
            list.Add(waiter);
        }
        try
        {
            await using var cancel = cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
            // Registered first, read second: no answer can slip in between unseen.
            await RecheckAsync(itemId, cancellationToken);
            return await waiter.Task;
        }
        finally
        {
            lock (_lock)
            {
                if (_waiting.TryGetValue(itemId, out var list) && list.Remove(waiter) && list.Count == 0)
                {
                    _waiting.Remove(itemId);
                }
            }
        }
    }

    public async Task RecheckAsync(Guid itemId, CancellationToken cancellationToken)
    {
        if (!IsWaited(itemId))
        {
            return;
        }
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TikaiDbContext>();
        var item = await db.AttentionItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == itemId, cancellationToken);
        if (item is not null && AttentionService.AnswerOf(item) is { } answer)
        {
            Complete(itemId, answer);
        }
    }

    private async Task OnEventAsync(BusEvent busEvent, CancellationToken cancellationToken)
    {
        if (busEvent.IsResync)
        {
            Guid[] waited;
            lock (_lock)
            {
                waited = [.. _waiting.Keys];
            }
            LogResync(waited.Length);
            foreach (var id in waited)
            {
                await RecheckAsync(id, cancellationToken);
            }
        }
        else
        {
            await RecheckAsync(busEvent.Id!.Value, cancellationToken);
        }
    }

    private bool IsWaited(Guid itemId)
    {
        lock (_lock)
        {
            return _waiting.ContainsKey(itemId);
        }
    }

    private void Complete(Guid itemId, AttentionAnswer answer)
    {
        TaskCompletionSource<AttentionAnswer>[] waiters;
        lock (_lock)
        {
            waiters = _waiting.TryGetValue(itemId, out var list) ? [.. list] : [];
        }
        foreach (var waiter in waiters)
        {
            waiter.TrySetResult(answer);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Event listener reconnected; reading {Count} waited attention item(s) again")]
    private partial void LogResync(int count);
}
