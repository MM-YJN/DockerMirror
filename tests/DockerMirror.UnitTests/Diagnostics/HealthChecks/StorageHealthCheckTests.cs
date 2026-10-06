using DockerMirror.Caching;
using DockerMirror.Configuration;
using DockerMirror.Diagnostics.HealthChecks;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DockerMirror.UnitTests.Diagnostics.HealthChecks;

public sealed class StorageHealthCheckTests : IAsyncDisposable
{
    private string? _tempDir;

    [Fact]
    public async Task ReturnsHealthy_WhenCachingDisabled()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        IOptions<MirrorOptions> options = Options.Create(new MirrorOptions { Cache = new CacheOptions { Enabled = false } });
        var store = new StoreWithoutProbe();
        var check = new StorageHealthCheck(store, options, NullLogger<StorageHealthCheck>.Instance);

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext(), ct);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task ReturnsHealthy_WhenFileSystemDirectoryExists()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        _tempDir = Path.Join(Path.GetTempPath(), "healthcheck-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        IOptions<MirrorOptions> optionsWrapper = Options.Create(new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                Backend = "FileSystem",
                FileSystem = new FileSystemCacheOptions { Directory = _tempDir },
            },
        });
        var store = new FileSystemContentStore(optionsWrapper, NullLogger<FileSystemContentStore>.Instance);
        var check = new StorageHealthCheck(store, optionsWrapper, NullLogger<StorageHealthCheck>.Instance);

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext(), ct);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task ReturnsUnhealthy_WhenFileSystemDirectoryMissing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string missingDir = Path.Join(Path.GetTempPath(), "healthcheck-missing-" + Guid.NewGuid().ToString("N"));
        IOptions<MirrorOptions> optionsWrapper = Options.Create(new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                Backend = "FileSystem",
                FileSystem = new FileSystemCacheOptions { Directory = missingDir },
            },
        });
        var store = new FileSystemContentStore(optionsWrapper, NullLogger<FileSystemContentStore>.Instance);
        var check = new StorageHealthCheck(store, optionsWrapper, NullLogger<StorageHealthCheck>.Instance);

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext(), ct);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task ReturnsHealthy_WhenStoreDoesNotImplementProbe()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        IOptions<MirrorOptions> optionsWrapper = Options.Create(new MirrorOptions { Cache = new CacheOptions { Enabled = true } });
        var store = new StoreWithoutProbe();
        var check = new StorageHealthCheck(store, optionsWrapper, NullLogger<StorageHealthCheck>.Instance);

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext(), ct);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    public async ValueTask DisposeAsync()
    {
        if (_tempDir is not null && Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }

        await ValueTask.CompletedTask;
    }

    private sealed class StoreWithoutProbe : IContentStore
    {
        public ValueTask<bool> ExistsAsync(string key, CancellationToken ct)
            => ValueTask.FromResult(false);

        public ValueTask<CachedContent?> TryGetAsync(string key, CancellationToken ct)
            => ValueTask.FromResult<CachedContent?>(null);

        public ValueTask<ICacheWriteHandle> BeginWriteAsync(string key, Digest expectedDigest, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
