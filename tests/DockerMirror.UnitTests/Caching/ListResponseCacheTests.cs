using DockerMirror.Caching;
using DockerMirror.Configuration;
using DockerMirror.Diagnostics;

using Microsoft.Extensions.Time.Testing;

namespace DockerMirror.UnitTests.Caching;

public sealed class ListResponseCacheTests : IDisposable
{
    private readonly MirrorMetrics _metrics = new();

    public void Dispose() => _metrics.Dispose();

    private ListResponseCache CreateCache(int maxEntries = 100, TimeProvider? timeProvider = null)
    {
        var listOptions = new ListCacheOptions { MaxEntries = maxEntries, MaxBodyBytes = 1024 * 1024 };
        return new TestListResponseCache(listOptions, _metrics, timeProvider ?? TimeProvider.System);
    }

    private static ListResponseCache.Entry CreateEntry(string body = "test", string? etag = null)
    {
        return new ListResponseCache.Entry
        {
            Body = System.Text.Encoding.UTF8.GetBytes(body),
            ContentType = "application/json",
            ETag = etag,
            Link = null,
            StoredAtUtc = DateTimeOffset.UtcNow,
        };
    }

    [Fact]
    public void TryGet_ReturnsFalse_WhenKeyNotCached()
    {
        ListResponseCache cache = CreateCache();

        bool found = cache.TryGet("missing", out _);

        Assert.False(found);
    }

    [Fact]
    public void StoreAndRetrieve_ReturnsCorrectEntry()
    {
        ListResponseCache cache = CreateCache();
        ListResponseCache.Entry entry = CreateEntry("hello");

        cache.Store("key1", entry, TimeSpan.FromSeconds(60));
        bool found = cache.TryGet("key1", out ListResponseCache.Entry? retrieved);

        Assert.True(found);
        Assert.NotNull(retrieved);
        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(retrieved.Body));
        Assert.Equal("application/json", retrieved.ContentType);
    }

    [Fact]
    public void Expired_ReturnsFalse()
    {
        var time = new FakeTimeProvider();
        ListResponseCache cache = CreateCache(timeProvider: time);

        cache.Store("key1", CreateEntry(), TimeSpan.FromSeconds(60));
        time.Advance(TimeSpan.FromSeconds(61));

        bool found = cache.TryGet("key1", out _);

        Assert.False(found);
    }

    [Fact]
    public void MultipleKeys_Independent()
    {
        ListResponseCache cache = CreateCache();
        ListResponseCache.Entry entry1 = CreateEntry("a");
        ListResponseCache.Entry entry2 = CreateEntry("b");

        cache.Store("key1", entry1, TimeSpan.FromSeconds(60));
        cache.Store("key2", entry2, TimeSpan.FromSeconds(60));

        Assert.True(cache.TryGet("key1", out ListResponseCache.Entry? r1));
        Assert.True(cache.TryGet("key2", out ListResponseCache.Entry? r2));
        Assert.Equal("a", System.Text.Encoding.UTF8.GetString(r1.Body));
        Assert.Equal("b", System.Text.Encoding.UTF8.GetString(r2.Body));
    }

    [Fact]
    public void Store_UpdateExisting_ReturnsUpdatedEntry()
    {
        ListResponseCache cache = CreateCache();
        ListResponseCache.Entry entry1 = CreateEntry("old");
        ListResponseCache.Entry entry2 = CreateEntry("new");

        cache.Store("key1", entry1, TimeSpan.FromSeconds(60));
        cache.Store("key1", entry2, TimeSpan.FromSeconds(60));

        bool found = cache.TryGet("key1", out ListResponseCache.Entry? retrieved);

        Assert.True(found);
        Assert.NotNull(retrieved);
        Assert.Equal("new", System.Text.Encoding.UTF8.GetString(retrieved.Body));
    }

    [Fact]
    public void RefreshExpiry_ExtendsLifetime()
    {
        var time = new FakeTimeProvider();
        ListResponseCache cache = CreateCache(timeProvider: time);

        cache.Store("key1", CreateEntry(), TimeSpan.FromSeconds(60));
        time.Advance(TimeSpan.FromSeconds(61));

        // Expired but still in the dictionary (TryGet does not remove expired entries).
        Assert.True(cache.TryGetExpiredEntry("key1", out _));
        Assert.False(cache.TryGet("key1", out _));

        // Refresh should revive it.
        bool refreshed = cache.RefreshExpiry("key1", TimeSpan.FromHours(1));

        Assert.True(refreshed);
        Assert.True(cache.TryGet("key1", out ListResponseCache.Entry? revived));
        Assert.NotNull(revived);
        Assert.Equal("test", System.Text.Encoding.UTF8.GetString(revived.Body));
    }

    [Fact]
    public void RefreshExpiry_MissingKey_ReturnsFalse()
    {
        ListResponseCache cache = CreateCache();

        bool refreshed = cache.RefreshExpiry("missing", TimeSpan.FromHours(1));

        Assert.False(refreshed);
    }

    [Fact]
    public void Trim_EvictsEntriesWhenOverMax()
    {
        ListResponseCache cache = CreateCache(maxEntries: 3);

        for (int i = 0; i < 10; i++)
        {
            cache.Store($"key{i}", CreateEntry(), TimeSpan.FromMinutes(10));
        }

        // Some keys must have been evicted. Count <= max.
        int live = 0;
        for (int i = 0; i < 10; i++)
        {
            if (cache.TryGet($"key{i}", out _))
            {
                live++;
            }
        }

        Assert.True(live < 10, $"Expected some evictions but all 10 entries are still present ({live} live).");
        Assert.True(live >= 1, $"Expected at least 1 entry to survive trimming.");
    }

    [Fact]
    public void Count_ReflectsLiveEntries()
    {
        ListResponseCache cache = CreateCache();

        Assert.Equal(0, cache.Count);

        cache.Store("key1", CreateEntry(), TimeSpan.FromSeconds(60));
        Assert.Equal(1, cache.Count);

        cache.Store("key2", CreateEntry(), TimeSpan.FromSeconds(60));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void TryGetExpiredEntry_ReturnsExpiredWithoutRemoving()
    {
        var time = new FakeTimeProvider();
        ListResponseCache cache = CreateCache(timeProvider: time);

        cache.Store("key1", CreateEntry(etag: "\"test-etag\""), TimeSpan.FromSeconds(60));
        time.Advance(TimeSpan.FromSeconds(61));

        // TryGet returns false but does NOT remove the expired entry.
        Assert.False(cache.TryGet("key1", out _));

        // TryGetExpiredEntry still finds the expired entry in the dictionary.
        bool found = cache.TryGetExpiredEntry("key1", out ListResponseCache.Entry? expired);
        Assert.True(found);
        Assert.NotNull(expired);
        Assert.Equal("\"test-etag\"", expired.ETag);

        // Calling again still works — the entry was not removed.
        Assert.True(cache.TryGetExpiredEntry("key1", out _));
    }

    [Fact]
    public void TryGetExpiredEntry_ReturnsFalse_WhenNotExpired()
    {
        ListResponseCache cache = CreateCache();
        cache.Store("key1", CreateEntry(), TimeSpan.FromHours(1));

        bool found = cache.TryGetExpiredEntry("key1", out _);

        Assert.False(found);
    }

    [Fact]
    public void TryGetExpiredEntry_ReturnsFalse_WhenNotCached()
    {
        ListResponseCache cache = CreateCache();

        bool found = cache.TryGetExpiredEntry("missing", out _);

        Assert.False(found);
    }

    // Concrete subclass for testing the abstract ListResponseCache base.
    private sealed class TestListResponseCache(ListCacheOptions options, MirrorMetrics metrics, TimeProvider timeProvider)
        : ListResponseCache(options, metrics, timeProvider, "dockermirror.list_cache.test_entries");
}
