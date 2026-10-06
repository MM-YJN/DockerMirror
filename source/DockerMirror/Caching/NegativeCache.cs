using System.Buffers;
using System.Collections.Concurrent;

using DockerMirror.Configuration;
using DockerMirror.Diagnostics;

using Microsoft.Extensions.Options;

namespace DockerMirror.Caching;

internal sealed class NegativeCache
{
    private readonly ConcurrentDictionary<string, long> _entries = new(StringComparer.Ordinal);
    private readonly int _maxEntries;
    private int _count;
    private int _trimming;

    // Approximate number of live entries; surfaced as an observable gauge.
    internal int Count => Volatile.Read(ref _count);

    public NegativeCache(IOptions<MirrorOptions> cacheOptions, MirrorMetrics metrics)
    {
        _maxEntries = cacheOptions.Value.Cache.NegativeCache.MaxEntries;
        metrics.RegisterNegativeCacheGauge(() => Count);
    }

    public static string BuildKey(string rewrittenName, string resourceType, string reference)
        => $"{rewrittenName}/{resourceType}/{reference}";

    public bool TryGet(string key)
    {
        if (!_entries.TryGetValue(key, out long expiryTicks))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow.Ticks >= expiryTicks)
        {
            if (_entries.TryRemove(key, out _))
            {
                Interlocked.Decrement(ref _count);
            }

            return false;
        }

        return true;
    }

    public void Store(string key, TimeSpan ttl)
    {
        long expiryTicks = DateTimeOffset.UtcNow.Ticks + ttl.Ticks;

        if (_entries.TryAdd(key, expiryTicks))
        {
            int newCount = Interlocked.Increment(ref _count);

            if (newCount > _maxEntries)
            {
                TrimExcess();
            }
        }
    }

    private void TrimExcess()
    {
        // Only one trim at a time; concurrent callers return immediately.
        if (Interlocked.CompareExchange(ref _trimming, 1, 0) != 0)
        {
            return;
        }

        try
        {
            int threshold = _maxEntries / 2;
            long now = DateTimeOffset.UtcNow.Ticks;

            // First pass: evict expired entries. Decrement only on successful removal to
            // avoid double-counting when concurrent trims would have raced on the same keys.
            foreach ((string? key, long expiry) in _entries)
            {
                if (now >= expiry && _entries.TryRemove(key, out _))
                {
                    Interlocked.Decrement(ref _count);
                }
            }

            int remaining = Volatile.Read(ref _count);

            if (remaining > threshold)
            {
                int toEvict = remaining - threshold;

                // Rent a buffer sized to the known entry cap. ArrayPool may return a larger
                // array; the guard inside the fill loop handles the rare case where concurrent
                // Store calls push _entries above maxEntries + 1 before we snapshot.
                KeyValuePair<string, long>[] snapshot = ArrayPool<KeyValuePair<string, long>>.Shared.Rent(_maxEntries + 1);
                try
                {
                    int n = 0;
                    foreach (KeyValuePair<string, long> kvp in _entries)
                    {
                        if (n < snapshot.Length)
                        {
                            snapshot[n++] = kvp;
                        }
                    }

                    // Sort ascending by expiry so the soonest-to-expire entries are evicted first.
                    Array.Sort(snapshot, 0, n, ExpiryComparer.Instance);

                    for (int i = 0; i < Math.Min(toEvict, n); i++)
                    {
                        if (_entries.TryRemove(snapshot[i].Key, out _))
                        {
                            Interlocked.Decrement(ref _count);
                        }
                    }
                }
                finally
                {
                    ArrayPool<KeyValuePair<string, long>>.Shared.Return(snapshot);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _trimming, 0);
        }
    }

    private sealed class ExpiryComparer : IComparer<KeyValuePair<string, long>>
    {
        public static readonly ExpiryComparer Instance = new();

        public int Compare(KeyValuePair<string, long> x, KeyValuePair<string, long> y) => x.Value.CompareTo(y.Value);
    }
}
