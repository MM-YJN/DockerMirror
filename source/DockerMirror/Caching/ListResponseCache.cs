using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

using DockerMirror.Configuration;
using DockerMirror.Diagnostics;

using Microsoft.Extensions.Options;

namespace DockerMirror.Caching;

// In-memory TTL cache for list responses (tags/list, _catalog). Expired entries are
// NOT removed on read — they remain in the dictionary so TryGetExpiredEntry can
// return them for upstream conditional revalidation. Cleanup happens in TrimExcess,
// which is triggered by Store when the entry count exceeds MaxEntries.
internal abstract class ListResponseCache
{
    internal sealed record Entry
    {
        public required byte[] Body { get; init; }
        public required string ContentType { get; init; }
        public string? ETag { get; init; }
        public string? Link { get; init; }
        public long ExpiryTicks { get; init; }
        public DateTimeOffset StoredAtUtc { get; init; }
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly int _maxEntries;
    private readonly long _maxBodyBytes;
    private readonly TimeProvider _timeProvider;
    private int _count;
    private int _trimming;

    // Approximate number of live entries; surfaced as an observable gauge.
    internal int Count => Volatile.Read(ref _count);

    protected ListResponseCache(ListCacheOptions options, MirrorMetrics metrics, TimeProvider timeProvider, string gaugeName)
    {
        _maxEntries = options.MaxEntries;
        _maxBodyBytes = options.MaxBodyBytes;
        _timeProvider = timeProvider;
        metrics.RegisterListCacheGauge(gaugeName, () => Count);
    }

    // Returns true only when the entry exists AND is still fresh. Does NOT remove
    // expired entries — they are left for TryGetExpiredEntry and TrimExcess.
    public bool TryGet(string key, [NotNullWhen(true)] out Entry? entry)
    {
        if (!_entries.TryGetValue(key, out Entry? existing))
        {
            entry = null;
            return false;
        }

        if (_timeProvider.GetUtcNow().Ticks >= existing.ExpiryTicks)
        {
            entry = null;
            return false;
        }

        entry = existing;
        return true;
    }

    // Returns an expired entry for revalidation purposes without removing it.
    // Used by ListResourceHandler to send an upstream If-None-Match with the stale ETag.
    public bool TryGetExpiredEntry(string key, [NotNullWhen(true)] out Entry? entry)
    {
        if (_entries.TryGetValue(key, out Entry? existing) &&
            _timeProvider.GetUtcNow().Ticks >= existing.ExpiryTicks)
        {
            entry = existing;
            return true;
        }

        entry = null;
        return false;
    }

    // Atomically adds or updates an entry. Uses TryAdd to avoid the closure-based
    // isNew race present with AddOrUpdate: during table resizing the add-factory
    // can be called (setting isNew = true) even though the final operation is an
    // update, causing _count to drift up.
    public void Store(string key, Entry entry, TimeSpan ttl)
    {
        long nowTicks = _timeProvider.GetUtcNow().Ticks;
        Entry finalEntry = entry with
        {
            ExpiryTicks = nowTicks + ttl.Ticks,
        };

        if (_entries.TryAdd(key, finalEntry))
        {
            if (Interlocked.Increment(ref _count) > _maxEntries)
            {
                TrimExcess();
            }
        }
        else
        {
            _entries[key] = finalEntry;
        }
    }

    public bool RefreshExpiry(string key, TimeSpan ttl)
    {
        if (!_entries.TryGetValue(key, out Entry? existing))
        {
            return false;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        Entry updated = existing with
        {
            ExpiryTicks = now.Ticks + ttl.Ticks,
            StoredAtUtc = now,
        };

        return _entries.TryUpdate(key, updated, existing);
    }

    public bool IsBodySizeOversized(long bodyLength) => bodyLength > _maxBodyBytes;

    private void TrimExcess()
    {
        if (Interlocked.CompareExchange(ref _trimming, 1, 0) != 0)
        {
            return;
        }

        try
        {
            int threshold = _maxEntries / 2;
            long now = _timeProvider.GetUtcNow().Ticks;

            // First pass: evict expired entries.
            foreach ((string? key, Entry? entry) in _entries)
            {
                if (now >= entry.ExpiryTicks && _entries.TryRemove(key, out _))
                {
                    Interlocked.Decrement(ref _count);
                }
            }

            int remaining = Volatile.Read(ref _count);

            if (remaining > threshold)
            {
                int toEvict = remaining - threshold;

                KeyValuePair<string, Entry>[] snapshot = ArrayPool<KeyValuePair<string, Entry>>.Shared.Rent(_maxEntries + 1);
                try
                {
                    int n = 0;
                    foreach (KeyValuePair<string, Entry> kvp in _entries)
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
                    ArrayPool<KeyValuePair<string, Entry>>.Shared.Return(snapshot, clearArray: true);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _trimming, 0);
        }
    }

    private sealed class ExpiryComparer : IComparer<KeyValuePair<string, Entry>>
    {
        public static readonly ExpiryComparer Instance = new();

        public int Compare(KeyValuePair<string, Entry> x, KeyValuePair<string, Entry> y)
            => x.Value.ExpiryTicks.CompareTo(y.Value.ExpiryTicks);
    }
}

internal sealed class TagsListResponseCache(IOptions<MirrorOptions> options, MirrorMetrics metrics, TimeProvider timeProvider)
    : ListResponseCache(options.Value.Cache.TagsList, metrics, timeProvider, "dockermirror.list_cache.tags_entries");

internal sealed class CatalogListResponseCache(IOptions<MirrorOptions> options, MirrorMetrics metrics, TimeProvider timeProvider)
    : ListResponseCache(options.Value.Cache.Catalog, metrics, timeProvider, "dockermirror.list_cache.catalog_entries");
