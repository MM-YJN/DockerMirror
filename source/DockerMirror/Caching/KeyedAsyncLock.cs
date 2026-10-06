using System.Collections.Concurrent;

using DockerMirror.Diagnostics;

namespace DockerMirror.Caching;

internal sealed partial class KeyedAsyncLock
{
    private readonly ILogger<KeyedAsyncLock> _logger;
    private readonly MirrorMetrics _metrics;
    private readonly ConcurrentDictionary<string, RefCountedSemaphore> _locks = new(StringComparer.Ordinal);
    private readonly object _cleanupLock = new();

    internal int Count => _locks.Count;

    public KeyedAsyncLock(ILogger<KeyedAsyncLock> logger, MirrorMetrics metrics)
    {
        _logger = logger;
        _metrics = metrics;
        _metrics.RegisterActiveLocksGauge(() => Count);
    }

    public async Task<IDisposable> LockAsync(string key, CancellationToken ct)
    {
        RefCountedSemaphore entry;

        lock (_cleanupLock)
        {
            entry = _locks.GetOrAdd(key, static _ => new RefCountedSemaphore(new SemaphoreSlim(1, 1)));
            Interlocked.Increment(ref entry._refCount);
        }

        if (entry._semaphore.CurrentCount == 0)
        {
            LogLockContended(key);
            _metrics.RecordLockContended();
        }

        try
        {
            await entry._semaphore.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            if (Interlocked.Decrement(ref entry._refCount) == 0)
            {
                Cleanup(key, entry);
            }

            throw;
        }

        return new Releaser(key, entry, this);
    }

    internal void ReleaseAndTryCleanup(string key, RefCountedSemaphore entry)
    {
        entry._semaphore.Release();

        if (Interlocked.Decrement(ref entry._refCount) == 0)
        {
            Cleanup(key, entry);
        }
    }

    private void Cleanup(string key, RefCountedSemaphore entry)
    {
        lock (_cleanupLock)
        {
            if (Volatile.Read(ref entry._refCount) == 0)
            {
                _locks.TryRemove(key, out _);
                Interlocked.Exchange(ref entry._refCount, -1);
                entry._semaphore.Dispose();
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Lock contended for {Key}; waiting.")]
    private partial void LogLockContended(string key);

    internal sealed class RefCountedSemaphore(SemaphoreSlim semaphore)
    {
        public readonly SemaphoreSlim _semaphore = semaphore;
        public int _refCount;
    }

    private sealed class Releaser(string key, RefCountedSemaphore entry, KeyedAsyncLock owner) : IDisposable
    {
        public void Dispose() => owner.ReleaseAndTryCleanup(key, entry);
    }
}
