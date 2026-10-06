using System.Net;

using DockerMirror.Caching;
using DockerMirror.Configuration;
using DockerMirror.Diagnostics;
using DockerMirror.Registry;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DockerMirror.UnitTests.Registry;

public sealed class NegativeCacheGateTests : IDisposable
{
    private readonly MirrorMetrics _metrics = new();

    public void Dispose() => _metrics.Dispose();

    // Creates a fully wired gate backed by a fresh NegativeCache.
    private NegativeCacheGate CreateGate(bool cacheEnabled = true, bool negCacheEnabled = true)
    {
        IOptions<MirrorOptions> opts = Options.Create(new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = cacheEnabled,
                NegativeCache = new NegativeCacheOptions { Enabled = negCacheEnabled },
            },
        });
        var cache = new NegativeCache(opts, _metrics);
        return new NegativeCacheGate(cache, opts, _metrics, NullLogger<NegativeCacheGate>.Instance);
    }

    // Creates a gate and returns the underlying NegativeCache so tests can assert on it directly.
    private (NegativeCacheGate Gate, NegativeCache Cache) CreateGateWithCache(
        bool cacheEnabled = true, bool negCacheEnabled = true)
    {
        IOptions<MirrorOptions> opts = Options.Create(new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = cacheEnabled,
                NegativeCache = new NegativeCacheOptions { Enabled = negCacheEnabled },
            },
        });
        var cache = new NegativeCache(opts, _metrics);
        return (new NegativeCacheGate(cache, opts, _metrics, NullLogger<NegativeCacheGate>.Instance), cache);
    }

    private static HttpContext CreateHttpContext()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    // ---- IsNegativeHit ----

    [Fact]
    public void IsNegativeHit_ReturnsFalse_WhenCacheDisabled()
    {
        NegativeCacheGate gate = CreateGate(cacheEnabled: false);

        Assert.False(gate.IsNegativeHit("library/nginx", "manifests", "latest"));
    }

    [Fact]
    public void IsNegativeHit_ReturnsFalse_WhenNegativeCacheDisabled()
    {
        NegativeCacheGate gate = CreateGate(negCacheEnabled: false);

        Assert.False(gate.IsNegativeHit("library/nginx", "manifests", "latest"));
    }

    [Fact]
    public void IsNegativeHit_ReturnsFalse_WhenNoEntryStored()
    {
        NegativeCacheGate gate = CreateGate();

        Assert.False(gate.IsNegativeHit("library/nginx", "manifests", "latest"));
    }

    [Fact]
    public void IsNegativeHit_ReturnsTrue_AfterStoreIfNegativeStoresEntry()
    {
        NegativeCacheGate gate = CreateGate();
        gate.StoreIfNegative(HttpStatusCode.NotFound, "library/nginx", "manifests", "latest");

        Assert.True(gate.IsNegativeHit("library/nginx", "manifests", "latest"));
    }

    // ---- StoreIfNegative ----

    [Fact]
    public void StoreIfNegative_IsNoOp_WhenCacheDisabled()
    {
        (NegativeCacheGate? gate, NegativeCache? cache) = CreateGateWithCache(cacheEnabled: false);

        gate.StoreIfNegative(HttpStatusCode.NotFound, "library/nginx", "manifests", "latest");

        // Inspect the underlying cache directly: nothing should have been stored.
        string key = NegativeCache.BuildKey("library/nginx", "manifests", "latest");
        Assert.False(cache.TryGet(key));
    }

    [Fact]
    public void StoreIfNegative_IsNoOp_WhenNegativeCacheDisabled()
    {
        (NegativeCacheGate? gate, NegativeCache? cache) = CreateGateWithCache(negCacheEnabled: false);

        gate.StoreIfNegative(HttpStatusCode.NotFound, "library/nginx", "blobs", "sha256:" + new string('a', 64));

        string key = NegativeCache.BuildKey("library/nginx", "blobs", "sha256:" + new string('a', 64));
        Assert.False(cache.TryGet(key));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    public void StoreIfNegative_StoresEntry_ForNotFoundAndGone(HttpStatusCode status)
    {
        NegativeCacheGate gate = CreateGate();
        string digest = "sha256:" + new string('a', 64);

        gate.StoreIfNegative(status, "library/nginx", "blobs", digest);

        Assert.True(gate.IsNegativeHit("library/nginx", "blobs", digest));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void StoreIfNegative_IsNoOp_ForNonNegativeStatusCodes(HttpStatusCode status)
    {
        NegativeCacheGate gate = CreateGate();

        gate.StoreIfNegative(status, "library/nginx", "manifests", "latest");

        Assert.False(gate.IsNegativeHit("library/nginx", "manifests", "latest"));
    }

    // ---- TryServe404Async ----

    [Fact]
    public async Task TryServe404Async_ReturnsFalse_WhenCacheDisabled()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        NegativeCacheGate gate = CreateGate(cacheEnabled: false);
        HttpContext ctx = CreateHttpContext();

        bool served = await gate.TryServe404Async(
            "library/nginx", "manifests", "latest", ctx.Response, writeBody: true, ct);

        Assert.False(served);
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode); // response untouched
    }

    [Fact]
    public async Task TryServe404Async_ReturnsFalse_WhenNoEntryStored()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        NegativeCacheGate gate = CreateGate();
        HttpContext ctx = CreateHttpContext();

        bool served = await gate.TryServe404Async(
            "library/nginx", "manifests", "latest", ctx.Response, writeBody: true, ct);

        Assert.False(served);
    }

    [Theory]
    [InlineData("manifests", "MANIFEST_UNKNOWN")]
    [InlineData("blobs", "BLOB_UNKNOWN")]
    public async Task TryServe404Async_Writes404_WithCorrectBody_WhenHit(string resourceType, string errorCode)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        NegativeCacheGate gate = CreateGate();
        string reference = "sha256:" + new string('a', 64);
        gate.StoreIfNegative(HttpStatusCode.NotFound, "library/nginx", resourceType, reference);
        HttpContext ctx = CreateHttpContext();

        bool served = await gate.TryServe404Async(
            "library/nginx", resourceType, reference, ctx.Response, writeBody: true, ct);

        Assert.True(served);
        Assert.Equal(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
        Assert.Equal(
            RegistryResponses.DockerDistributionApiVersionValue,
            ctx.Response.Headers[RegistryResponses.DockerDistributionApiVersion].ToString());
        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        string body = await new StreamReader(ctx.Response.Body).ReadToEndAsync(ct);
        Assert.Contains(errorCode, body);
    }

    [Fact]
    public async Task TryServe404Async_WritesNoBody_WhenWriteBodyFalse()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        NegativeCacheGate gate = CreateGate();
        gate.StoreIfNegative(HttpStatusCode.NotFound, "library/nginx", "manifests", "latest");
        HttpContext ctx = CreateHttpContext();

        bool served = await gate.TryServe404Async(
            "library/nginx", "manifests", "latest", ctx.Response, writeBody: false, ct);

        Assert.True(served);
        Assert.Equal(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
        Assert.Equal(0L, ctx.Response.Body.Length);
    }
}
