using System.Collections.Concurrent;

namespace Tikai.Api.Hosting;

/// <summary>
/// One holder per key at a time, within this process (the hub is one process). The semaphores are few (one per
/// repository or run that was locked) and kept.
/// </summary>
internal abstract class KeyedLocks
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public async Task<IDisposable> AcquireAsync(Guid key, CancellationToken cancellationToken)
    {
        var semaphore = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        return new Release(semaphore);
    }

    private sealed class Release(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
