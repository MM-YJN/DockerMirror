using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

using DockerMirror.Caching;
using DockerMirror.Configuration;
using DockerMirror.Diagnostics;
using DockerMirror.Registry;
using DockerMirror.UnitTests.TestInfrastructure;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DockerMirror.UnitTests.Caching;

public sealed class CacheEvictionServiceTests : IAsyncDisposable
{
    private readonly string _cacheDir;
    private readonly MirrorMetrics _metrics = new();

    public CacheEvictionServiceTests()
        => _cacheDir = Path.Join(Path.GetTempPath(), "eviction-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Sweep_EvictsMaxAgeEntries()
    {
        var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(now);
        var store = new FakeMaintenanceStore();
        store.AddEntry(TestKey("aa"), 1000, now.AddHours(-2));
        store.AddEntry(TestKey("bb"), 2000, now.AddMinutes(-5));

        var options = new CacheEvictionOptions
        {
            Enabled = true,
            Strategy = EvictionStrategy.Oldest,
            MaxAge = TimeSpan.FromHours(1),
            MaxSizeBytes = null,
        };

        CacheEvictionService service = CreateService(store, options, time, out CacheStatsState? cacheStats);

        await InvokeSweepAsync(service, store, options);

        Assert.Single(store.DeletedKeys);
        Assert.Contains(TestKey("aa"), store.DeletedKeys);
        Assert.DoesNotContain(TestKey("bb"), store.DeletedKeys);
        Assert.Equal(2000, cacheStats.SizeBytes);
        Assert.Equal(1, cacheStats.EntryCount);
        Assert.NotEqual(DateTimeOffset.MinValue, cacheStats.LastUpdateUtc);
    }

    [Fact]
    public async Task Sweep_EvictsOldestEntries_WhenOverSizeCap()
    {
        var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(now);
        var store = new FakeMaintenanceStore();
        store.AddEntry(TestKey("oldest"), 1000, now.AddHours(-3));
        store.AddEntry(TestKey("middle"), 1000, now.AddHours(-2));
        store.AddEntry(TestKey("newest"), 1000, now.AddHours(-1));

        var options = new CacheEvictionOptions
        {
            Enabled = true,
            Strategy = EvictionStrategy.Oldest,
            MaxSizeBytes = 1500,
            MaxAge = null,
            TargetUtilization = 1.0,
        };

        CacheEvictionService service = CreateService(store, options, time);

        await InvokeSweepAsync(service, store, options);

        Assert.Equal(2, store.DeletedKeys.Count);
        Assert.Contains(TestKey("oldest"), store.DeletedKeys);
        Assert.Contains(TestKey("middle"), store.DeletedKeys);
        Assert.DoesNotContain(TestKey("newest"), store.DeletedKeys);
    }

    [Fact]
    public async Task Sweep_EvictsToTargetUtilization()
    {
        var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(now);
        var store = new FakeMaintenanceStore();
        for (int i = 0; i < 10; i++)
        {
            store.AddEntry(TestKey($"pkg{i}"), 1000, now.AddHours(-1).AddMinutes(i));
        }

        var options = new CacheEvictionOptions
        {
            Enabled = true,
            Strategy = EvictionStrategy.Oldest,
            MaxSizeBytes = 9000,
            MaxAge = null,
            TargetUtilization = 0.5,
        };

        CacheEvictionService service = CreateService(store, options, time);

        await InvokeSweepAsync(service, store, options);

        Assert.Equal(6, store.DeletedKeys.Count);
    }

    [Fact]
    public async Task Sweep_AppliesMaxAgeBeforeSizeCap()
    {
        var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(now);
        var store = new FakeMaintenanceStore();
        // Pre-age total = 3500 bytes, which exceeds MaxSizeBytes (1000).
        // If the size pass used the pre-age total it would incorrectly delete 'recent'.
        // After the age pass removes old1+old2, remaining = 500 bytes ≤ 1000, so the
        // size pass must not fire — 'recent' survives only if age runs before size.
        store.AddEntry(TestKey("old1"), 2000, now.AddHours(-3));
        store.AddEntry(TestKey("old2"), 1000, now.AddHours(-3));
        store.AddEntry(TestKey("recent"), 500, now.AddMinutes(-5));

        var options = new CacheEvictionOptions
        {
            Enabled = true,
            Strategy = EvictionStrategy.Oldest,
            MaxAge = TimeSpan.FromHours(1),
            MaxSizeBytes = 1000,
            TargetUtilization = 1.0,
        };

        CacheEvictionService service = CreateService(store, options, time);

        await InvokeSweepAsync(service, store, options);

        Assert.Contains(TestKey("old1"), store.DeletedKeys);
        Assert.Contains(TestKey("old2"), store.DeletedKeys);
        // 'recent' must survive: the size pass recomputes from the post-age list
        // (500 bytes ≤ 1000) and finds no action needed.
        Assert.DoesNotContain(TestKey("recent"), store.DeletedKeys);
        Assert.Equal(2, store.DeletedKeys.Count);
    }

    [Fact]
    public async Task Sweep_AllEntriesWithinBounds_NoEviction()
    {
        var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(now);
        var store = new FakeMaintenanceStore();
        store.AddEntry(TestKey("a"), 1000, now.AddMinutes(-5));
        store.AddEntry(TestKey("b"), 2000, now.AddMinutes(-10));

        var options = new CacheEvictionOptions
        {
            Enabled = true,
            MaxAge = TimeSpan.FromHours(1),
            MaxSizeBytes = 5000,
        };

        CacheEvictionService service = CreateService(store, options, time, out CacheStatsState? cacheStats);

        await InvokeSweepAsync(service, store, options);

        Assert.Empty(store.DeletedKeys);
        Assert.Equal(3000, cacheStats.SizeBytes);
        Assert.Equal(2, cacheStats.EntryCount);
        Assert.NotEqual(DateTimeOffset.MinValue, cacheStats.LastUpdateUtc);
    }

    [Fact]
    public async Task Sweep_SizeCap_OldestStrategy_EvictsByCreatedAtUtc()
    {
        var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(now);
        var store = new FakeMaintenanceStore();
        // Entry older by CreatedAtUtc but more recently accessed (should be evicted under Oldest).
        store.AddEntry(TestKey("older_created"), 1000, now.AddHours(-3), now.AddMinutes(-1));
        // Entry newer by CreatedAtUtc but less recently accessed (should SURVIVE under Oldest).
        store.AddEntry(TestKey("newer_created"), 1000, now.AddHours(-1), now.AddHours(-2));

        var options = new CacheEvictionOptions
        {
            Enabled = true,
            Strategy = EvictionStrategy.Oldest,
            MaxSizeBytes = 1500,
            MaxAge = null,
            TargetUtilization = 1.0,
        };

        CacheEvictionService service = CreateService(store, options, time);

        await InvokeSweepAsync(service, store, options);

        Assert.Single(store.DeletedKeys);
        Assert.Contains(TestKey("older_created"), store.DeletedKeys);
        Assert.DoesNotContain(TestKey("newer_created"), store.DeletedKeys);
    }

    [Fact]
    public async Task Sweep_SizeCap_LruStrategy_EvictsByLastAccessedUtc()
    {
        var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(now);
        var store = new FakeMaintenanceStore();
        // Entry older by CreatedAtUtc but more recently accessed (should SURVIVE under LRU).
        store.AddEntry(TestKey("older_created"), 1000, now.AddHours(-3), now.AddMinutes(-1));
        // Entry newer by CreatedAtUtc but less recently accessed (should be evicted under LRU).
        store.AddEntry(TestKey("newer_created"), 1000, now.AddHours(-1), now.AddHours(-2));

        var options = new CacheEvictionOptions
        {
            Enabled = true,
            Strategy = EvictionStrategy.Lru,
            MaxSizeBytes = 1500,
            MaxAge = null,
            TargetUtilization = 1.0,
        };

        CacheEvictionService service = CreateService(store, options, time);

        await InvokeSweepAsync(service, store, options);

        Assert.Single(store.DeletedKeys);
        Assert.Contains(TestKey("newer_created"), store.DeletedKeys);
        Assert.DoesNotContain(TestKey("older_created"), store.DeletedKeys);
    }

    [Theory]
    [InlineData(EvictionStrategy.Oldest)]
    [InlineData(EvictionStrategy.Lru)]
    public async Task Sweep_MaxAge_UsesCreatedAtUtc_RegardlessOfStrategy(EvictionStrategy strategy)
    {
        var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(now);
        var store = new FakeMaintenanceStore();
        // Old CreatedAtUtc but recent LastAccessedUtc — must be evicted under MaxAge.
        store.AddEntry(TestKey("old_created_recent_access"), 1000, now.AddHours(-3), now.AddMinutes(-1));
        // Recent CreatedAtUtc but old LastAccessedUtc — must survive MaxAge.
        store.AddEntry(TestKey("recent_created_old_access"), 1000, now.AddMinutes(-5), now.AddHours(-2));

        var options = new CacheEvictionOptions
        {
            Enabled = true,
            Strategy = strategy,
            MaxAge = TimeSpan.FromHours(1),
            MaxSizeBytes = null,
        };

        CacheEvictionService service = CreateService(store, options, time);

        await InvokeSweepAsync(service, store, options);

        Assert.Single(store.DeletedKeys);
        Assert.Contains(TestKey("old_created_recent_access"), store.DeletedKeys);
        Assert.DoesNotContain(TestKey("recent_created_old_access"), store.DeletedKeys);
    }

    [Fact]
    public async Task Sweep_NoOp_WhenDisabled()
    {
        var store = new FakeMaintenanceStore();
        store.AddEntry(TestKey("a"), 1000, DateTimeOffset.UtcNow.AddHours(-3));

        var cacheOptions = new CacheOptions
        {
            Enabled = true,
            Eviction = new CacheEvictionOptions
            {
                Enabled = false,
                MaxAge = TimeSpan.FromHours(1),
            },
        };

        var service = new CacheEvictionService(
            store,
            Options.Create(new MirrorOptions { Cache = cacheOptions }),
            TimeProvider.System,
            NullLogger<CacheEvictionService>.Instance,
            _metrics,
            new CacheStatsState());

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Empty(store.DeletedKeys);
    }

    [Fact]
    public async Task Sweep_NoOp_WhenCacheDisabled()
    {
        var store = new FakeMaintenanceStore();
        store.AddEntry(TestKey("a"), 1000, DateTimeOffset.UtcNow.AddHours(-3));

        var cacheOptions = new CacheOptions
        {
            Enabled = false,
            Eviction = new CacheEvictionOptions
            {
                Enabled = true,
                MaxAge = TimeSpan.FromHours(1),
            },
        };

        var service = new CacheEvictionService(
            store,
            Options.Create(new MirrorOptions { Cache = cacheOptions }),
            TimeProvider.System,
            NullLogger<CacheEvictionService>.Instance,
            _metrics,
            new CacheStatsState());

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Empty(store.DeletedKeys);
    }

    [Fact]
    public async Task Sweep_NoOp_WhenStoreNotICacheMaintenance()
    {
        var store = new FakeStoreNoMaintenance();
        var cacheOptions = new CacheOptions
        {
            Enabled = true,
            Eviction = new CacheEvictionOptions
            {
                Enabled = true,
                MaxAge = TimeSpan.FromHours(1),
            },
        };

        var service = new CacheEvictionService(
            store,
            Options.Create(new MirrorOptions { Cache = cacheOptions }),
            TimeProvider.System,
            NullLogger<CacheEvictionService>.Instance,
            _metrics,
            new CacheStatsState());

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        // Verify the service handled the non-maintenance store gracefully without faulting.
        Assert.NotNull(service.ExecuteTask);
        Assert.False(service.ExecuteTask.IsFaulted);
    }

    [Fact]
    public async Task Sweep_ToleratesDeleteErrors()
    {
        var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(now);
        var store = new FakeErroringStore();
        store.AddEntry(TestKey("a"), 1000, now.AddHours(-2));
        store.AddEntry(TestKey("b"), 1000, now.AddHours(-2));

        var options = new CacheEvictionOptions
        {
            Enabled = true,
            MaxAge = TimeSpan.FromHours(1),
        };

        CacheEvictionService service = CreateService(store, options, time);

        await InvokeSweepAsync(service, store, options);
    }

    [Fact]
    public async Task Touch_OnHit_OnlyWhenLruStrategy_CalledForPreLockHit()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] content = "test-touch-content"u8.ToArray();
        string digestStr = ComputeSha256Digest(content);
        var digest = DockerMirror.Caching.Digest.Parse(digestStr);
        string cacheKey = DigestCacheKey.FromDigest(digest);
        string path = $"library/nginx/blobs/{digestStr}";
        var store = new FakeMaintenanceStore();

        // Pre-populate the cache so the first TryGetAsync returns a hit.
        store.SetContent(cacheKey, content, digestStr);

        var upstreamHandler = new TestHttpMessageHandler(_ =>
            new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            });
        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(upstreamHandler)
            .WithCacheOptions(new CacheOptions
            {
                Enabled = true,
                Eviction = new CacheEvictionOptions { Enabled = true, Strategy = EvictionStrategy.Lru },
            })
            .WithStore(store)
            .Build();

        HttpContext ctx = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, ctx, ct);

        Assert.True(ctx.Response.StatusCode == StatusCodes.Status200OK);
        Assert.Single(store.TouchedKeys);
    }

    [Fact]
    public async Task Touch_OnHit_NotCalledWhenOldestStrategy()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] content = "test-touch-noop"u8.ToArray();
        string digestStr = ComputeSha256Digest(content);
        var digest = DockerMirror.Caching.Digest.Parse(digestStr);
        string cacheKey = DigestCacheKey.FromDigest(digest);
        string path = $"library/nginx/blobs/{digestStr}";
        var store = new FakeMaintenanceStore();

        store.SetContent(cacheKey, content, digestStr);

        var upstreamHandler = new TestHttpMessageHandler(_ =>
            new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            });
        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(upstreamHandler)
            .WithCacheOptions(new CacheOptions
            {
                Enabled = true,
                Eviction = new CacheEvictionOptions { Enabled = true, Strategy = EvictionStrategy.Oldest },
            })
            .WithStore(store)
            .Build();

        HttpContext ctx = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, ctx, ct);

        Assert.Empty(store.TouchedKeys);
    }

    [Fact]
    public async Task Touch_OnHit_LruStrategy_CalledForHeadHit()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] content = "test-touch-head"u8.ToArray();
        string digestStr = ComputeSha256Digest(content);
        var digest = DockerMirror.Caching.Digest.Parse(digestStr);
        string cacheKey = DigestCacheKey.FromDigest(digest);
        string path = $"library/nginx/blobs/{digestStr}";
        var store = new FakeMaintenanceStore();

        store.SetContent(cacheKey, content, digestStr);

        var upstreamHandler = new TestHttpMessageHandler(_ =>
            new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            });
        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(upstreamHandler)
            .WithCacheOptions(new CacheOptions
            {
                Enabled = true,
                Eviction = new CacheEvictionOptions { Enabled = true, Strategy = EvictionStrategy.Lru },
            })
            .WithStore(store)
            .Build();

        HttpContext ctx = CachingRegistryServiceBuilder.CreateHttpContext("HEAD", path);
        await service.HandleAsync(path, ctx, ct);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.Single(store.TouchedKeys);
    }

    [Fact]
    public async Task Touch_OnHit_NotCalledWhenEvictionDisabled()
    {
        // TouchOnHitAsync must short-circuit when Eviction.Enabled=false, even if
        // Strategy=Lru, because the user has not opted in to LRU tracking.
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] content = "test-touch-eviction-off"u8.ToArray();
        string digestStr = ComputeSha256Digest(content);
        var digest = DockerMirror.Caching.Digest.Parse(digestStr);
        string cacheKey = DigestCacheKey.FromDigest(digest);
        string path = $"library/nginx/blobs/{digestStr}";
        var store = new FakeMaintenanceStore();

        store.SetContent(cacheKey, content, digestStr);

        var upstreamHandler = new TestHttpMessageHandler(_ =>
            new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            });
        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(upstreamHandler)
            .WithCacheOptions(new CacheOptions
            {
                Enabled = true,
                Eviction = new CacheEvictionOptions { Enabled = false, Strategy = EvictionStrategy.Lru },
            })
            .WithStore(store)
            .Build();

        HttpContext ctx = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, ctx, ct);

        Assert.Empty(store.TouchedKeys);
    }

    [Fact]
    public async Task TouchAsync_UpdatesLastWriteTime()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FileSystemContentStore store = CreateStore();
        byte[] content = "touch-store-test"u8.ToArray();
        Digest digest = ComputeDigest(content);
        string key = DigestCacheKey.FromDigest(digest);

        await WriteAndCommit(store, content, digest, "application/octet-stream", ct);

        string contentPath = Path.Join(_cacheDir, key.Replace('/', Path.DirectorySeparatorChar));

        // Push mtime into the past
        DateTime pastTime = DateTime.UtcNow.AddHours(-1);
        File.SetLastWriteTimeUtc(contentPath, pastTime);

        // Touch should bump it forward
        await store.TouchAsync(key, ct);

        DateTime newMtime = File.GetLastWriteTimeUtc(contentPath);
        Assert.True(newMtime > pastTime, $"Expected {newMtime} > {pastTime}");
    }

    [Fact]
    public async Task EnumerateStatsOnly_UpdatesCacheStats()
    {
        var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(now);
        var store = new FakeMaintenanceStore();
        store.AddEntry(TestKey("a"), 1000, now.AddMinutes(-5));
        store.AddEntry(TestKey("b"), 2000, now.AddMinutes(-10));

        var cacheOptions = new CacheOptions
        {
            Enabled = true,
            Eviction = new CacheEvictionOptions { Enabled = false },
            SizeReporting = new CacheSizeReportingOptions { Enabled = true },
        };

        var stats = new CacheStatsState();
        var service = new CacheEvictionService(
            store,
            Options.Create(new MirrorOptions { Cache = cacheOptions }),
            time,
            NullLogger<CacheEvictionService>.Instance,
            _metrics,
            stats);

        await InvokeEnumerateStatsOnlyAsync(service, store);

        Assert.Equal(3000, stats.SizeBytes);
        Assert.Equal(2, stats.EntryCount);
        Assert.NotEqual(DateTimeOffset.MinValue, stats.LastUpdateUtc);
    }

    [Fact]
    public async Task EnumerateStatsOnly_EmptyCache_ReportsZero()
    {
        var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(now);
        var store = new FakeMaintenanceStore();

        var cacheOptions = new CacheOptions
        {
            Enabled = true,
            Eviction = new CacheEvictionOptions { Enabled = false },
            SizeReporting = new CacheSizeReportingOptions { Enabled = true },
        };

        var stats = new CacheStatsState();
        var service = new CacheEvictionService(
            store,
            Options.Create(new MirrorOptions { Cache = cacheOptions }),
            time,
            NullLogger<CacheEvictionService>.Instance,
            _metrics,
            stats);

        await InvokeEnumerateStatsOnlyAsync(service, store);

        Assert.Equal(0, stats.SizeBytes);
        Assert.Equal(0, stats.EntryCount);
        Assert.NotEqual(DateTimeOffset.MinValue, stats.LastUpdateUtc);
    }

    private static string TestKey(string suffix)
    {
        return $"sha256/te/st/{suffix.PadRight(64, '0').AsSpan(0, 64)}";
    }

    private static string ComputeSha256Digest(byte[] content)
    {
        byte[] hash = SHA256.HashData(content);
        return $"sha256:{Convert.ToHexStringLower(hash)}";
    }

    private static Digest ComputeDigest(byte[] content)
    {
        byte[] hash = SHA256.HashData(content);
        return new Digest("sha256", Convert.ToHexStringLower(hash));
    }

    private CacheEvictionService CreateService(
        IContentStore store,
        CacheEvictionOptions eviction,
        TimeProvider time,
        out CacheStatsState cacheStats)
    {
        var cacheOptions = new CacheOptions
        {
            Enabled = true,
            Eviction = eviction,
        };

        cacheStats = new CacheStatsState();

        return new CacheEvictionService(
            store,
            Options.Create(new MirrorOptions { Cache = cacheOptions }),
            time,
            NullLogger<CacheEvictionService>.Instance,
            _metrics,
            cacheStats);
    }

    private CacheEvictionService CreateService(
        IContentStore store,
        CacheEvictionOptions eviction,
        TimeProvider time)
    {
        return CreateService(store, eviction, time, out _);
    }

    private static async Task InvokeSweepAsync(
        CacheEvictionService service,
        ICacheMaintenance maintenance,
        CacheEvictionOptions eviction)
    {
        MethodInfo? method = typeof(CacheEvictionService).GetMethod(
            "SweepAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        object? result = method.Invoke(service, [maintenance, eviction, TestContext.Current.CancellationToken]);
        Task task = Assert.IsAssignableFrom<Task>(result);
        await task;
    }

    private static async Task InvokeEnumerateStatsOnlyAsync(
        CacheEvictionService service,
        ICacheMaintenance maintenance)
    {
        MethodInfo? method = typeof(CacheEvictionService).GetMethod(
            "EnumerateStatsOnlyAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        object? result = method.Invoke(service, [maintenance, TestContext.Current.CancellationToken]);
        Task task = Assert.IsAssignableFrom<Task>(result);
        await task;
    }

    private FileSystemContentStore CreateStore()
    {
        IOptions<MirrorOptions> options = Options.Create(new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                FileSystem = new FileSystemCacheOptions { Directory = _cacheDir },
            },
        });
        return new FileSystemContentStore(options, NullLogger<FileSystemContentStore>.Instance);
    }

    private static async Task WriteAndCommit(
        IContentStore store, byte[] content, Digest expectedDigest, string contentType, CancellationToken ct)
    {
        string key = DigestCacheKey.FromDigest(expectedDigest);
        await using ICacheWriteHandle handle = await store.BeginWriteAsync(key, expectedDigest, ct);
        handle.SetMetadata(new CacheEntryMetadata(contentType));
        await handle.Stream.WriteAsync(content, ct);
        await handle.CommitAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        _metrics.Dispose();
        try
        {
            if (Directory.Exists(_cacheDir))
            {
                Directory.Delete(_cacheDir, recursive: true);
            }
        }
        catch
        {
        }

        await ValueTask.CompletedTask;
        GC.SuppressFinalize(this);
    }

    private sealed class FakeMaintenanceStore : IContentStore, ICacheMaintenance
    {
        private readonly List<CacheEntryInfo> _entries = [];
        private readonly Dictionary<string, (byte[] Content, string Digest)> _contents = [];

        public List<string> DeletedKeys { get; } = [];
        public List<string> TouchedKeys { get; } = [];

        public void AddEntry(string key, long length, DateTimeOffset createdAtUtc)
            => AddEntry(key, length, createdAtUtc, createdAtUtc);

        public void AddEntry(string key, long length, DateTimeOffset createdAtUtc, DateTimeOffset lastAccessedUtc)
            => _entries.Add(new CacheEntryInfo(key, length, createdAtUtc, lastAccessedUtc));

        public void SetContent(string key, byte[] content, string digest)
            => _contents[key] = (content, digest);

        public ValueTask<bool> ExistsAsync(string key, CancellationToken ct)
            => ValueTask.FromResult(_contents.ContainsKey(key));

        public ValueTask<CachedContent?> TryGetAsync(string key, CancellationToken ct)
        {
            if (_contents.TryGetValue(key, out (byte[] Content, string Digest) stored))
            {
                var stream = new MemoryStream(stored.Content);
                return ValueTask.FromResult<CachedContent?>(new CachedContent
                {
                    Stream = stream,
                    Length = stored.Content.Length,
                    ContentType = "application/octet-stream",
                    Digest = stored.Digest,
                });
            }

            return ValueTask.FromResult<CachedContent?>(null);
        }

        public ValueTask<ICacheWriteHandle> BeginWriteAsync(string key, Digest expectedDigest, CancellationToken ct)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<CacheEntryInfo> EnumerateAsync([EnumeratorCancellation] CancellationToken ct)
        {
            foreach (CacheEntryInfo entry in _entries)
            {
                ct.ThrowIfCancellationRequested();
                yield return entry;
            }

            await Task.CompletedTask;
        }

        public ValueTask DeleteAsync(string key, CancellationToken ct)
        {
            DeletedKeys.Add(key);
            return ValueTask.CompletedTask;
        }

        public ValueTask TouchAsync(string key, CancellationToken ct)
        {
            TouchedKeys.Add(key);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeStoreNoMaintenance : IContentStore
    {
        public ValueTask<bool> ExistsAsync(string key, CancellationToken ct)
            => ValueTask.FromResult(false);

        public ValueTask<CachedContent?> TryGetAsync(string key, CancellationToken ct)
            => ValueTask.FromResult<CachedContent?>(null);

        public ValueTask<ICacheWriteHandle> BeginWriteAsync(string key, Digest expectedDigest, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class FakeErroringStore : IContentStore, ICacheMaintenance
    {
        private readonly List<CacheEntryInfo> _entries = [];

        public void AddEntry(string key, long length, DateTimeOffset createdAtUtc)
            => AddEntry(key, length, createdAtUtc, createdAtUtc);

        public void AddEntry(string key, long length, DateTimeOffset createdAtUtc, DateTimeOffset lastAccessedUtc)
            => _entries.Add(new CacheEntryInfo(key, length, createdAtUtc, lastAccessedUtc));

        public ValueTask<bool> ExistsAsync(string key, CancellationToken ct)
            => ValueTask.FromResult(false);

        public ValueTask<CachedContent?> TryGetAsync(string key, CancellationToken ct)
            => ValueTask.FromResult<CachedContent?>(null);

        public ValueTask<ICacheWriteHandle> BeginWriteAsync(string key, Digest expectedDigest, CancellationToken ct)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<CacheEntryInfo> EnumerateAsync([EnumeratorCancellation] CancellationToken ct)
        {
            foreach (CacheEntryInfo entry in _entries)
            {
                ct.ThrowIfCancellationRequested();
                yield return entry;
            }

            await Task.CompletedTask;
        }

        public ValueTask DeleteAsync(string key, CancellationToken ct)
            => ValueTask.FromException(new InvalidOperationException("Simulated delete failure"));

        public ValueTask TouchAsync(string key, CancellationToken ct)
            => ValueTask.CompletedTask;
    }
}
