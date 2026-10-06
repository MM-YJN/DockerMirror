using DockerMirror.Caching;
using DockerMirror.Configuration;
using DockerMirror.Diagnostics;

using Microsoft.Extensions.Options;

namespace DockerMirror.UnitTests.Caching;

public sealed class NegativeCacheTests : IDisposable
{
    private readonly MirrorMetrics _metrics = new();

    public void Dispose() => _metrics.Dispose();

    private static NegativeCache CreateCache(int maxEntries = 100)
        => new(Options.Create(new MirrorOptions { Cache = new CacheOptions { NegativeCache = new NegativeCacheOptions { MaxEntries = maxEntries } } }), new MirrorMetrics());

    [Fact]
    public void TryGet_ReturnsFalse_WhenKeyNotCached()
    {
        NegativeCache cache = CreateCache();

        bool result = cache.TryGet("missing");

        Assert.False(result);
    }

    [Fact]
    public void StoreAndRetrieve_ReturnsTrue()
    {
        NegativeCache cache = CreateCache();

        cache.Store("key1", TimeSpan.FromSeconds(60));
        bool result = cache.TryGet("key1");

        Assert.True(result);
    }

    [Fact]
    public async Task Expired_ReturnsFalse()
    {
        NegativeCache cache = CreateCache();

        cache.Store("key1", TimeSpan.FromMilliseconds(1));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        bool result = cache.TryGet("key1");

        Assert.False(result);
    }

    [Fact]
    public void MultipleKeys_Independent()
    {
        NegativeCache cache = CreateCache();

        cache.Store("key1", TimeSpan.FromSeconds(60));
        cache.Store("key2", TimeSpan.FromSeconds(60));

        Assert.True(cache.TryGet("key1"));
        Assert.True(cache.TryGet("key2"));
        Assert.False(cache.TryGet("key3"));
    }

    [Fact]
    public async Task Store_SameKey_DoesNotRefreshTtl()
    {
        NegativeCache cache = CreateCache();

        // First write with a very short TTL wins.
        cache.Store("key1", TimeSpan.FromMilliseconds(1));
        // Second write with a longer TTL must not extend the expiry.
        cache.Store("key1", TimeSpan.FromSeconds(60));

        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(cache.TryGet("key1"));
    }

    [Fact]
    public void Evicts_WhenMaxEntriesExceeded()
    {
        // Add many entries: each time count exceeds maxEntries, TrimExcess cuts to maxEntries/2.
        // After multiple trim cycles, the final count should be well below the total added.
        NegativeCache cache = CreateCache(maxEntries: 10);
        int keysToAdd = 30;

        for (int i = 0; i < keysToAdd; i++)
        {
            cache.Store($"k{i}", TimeSpan.FromMinutes(10));
        }

        int found = 0;
        for (int i = 0; i < keysToAdd; i++)
        {
            if (cache.TryGet($"k{i}"))
            {
                found++;
            }
        }

        Assert.True(found < 20, $"Trim should have removed many entries, found {found}");
        Assert.True(found > 0, "Expected at least some entries to survive");
    }
}
