using System.Net;
using System.Security.Cryptography;

using DockerMirror.Caching;
using DockerMirror.Configuration;
using DockerMirror.Diagnostics;
using DockerMirror.Registry;
using DockerMirror.UnitTests.TestInfrastructure;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace DockerMirror.UnitTests.Registry;

public sealed class CachingRegistryServiceTests : IAsyncDisposable
{
    private readonly string _cacheDir;
    private readonly MirrorMetrics _metrics = new();

    public CachingRegistryServiceTests()
        => _cacheDir = Path.Join(Path.GetTempPath(), "caching-registry-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CacheMiss_FetchesAndCaches_SecondRequestServesFromCache()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] content = "hello-from-upstream"u8.ToArray();
        string digest = ComputeSha256Digest(content);
        string path = $"library/nginx/blobs/{digest}";

        int callCount = 0;
        var upstreamHandler = new TestHttpMessageHandler(request =>
        {
            Interlocked.Increment(ref callCount);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            };
        });

        FileSystemContentStore store = CreateStore();
        var keyedLock = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance, _metrics);

        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(upstreamHandler)
            .WithStore(store)
            .WithKeyedLock(keyedLock)
            .Build();

        // First request: miss, should cache
        HttpContext ctx1 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        IResult result1 = await service.HandleAsync(path, ctx1, ct);
        Assert.Equal(StatusCodes.Status200OK, ctx1.Response.StatusCode);
        Assert.Equal(1, Volatile.Read(ref callCount));

        // Second request: should be a cache hit
        HttpContext ctx2 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        IResult result2 = await service.HandleAsync(path, ctx2, ct);
        Assert.Equal(StatusCodes.Status200OK, ctx2.Response.StatusCode);
        Assert.Equal(1, Volatile.Read(ref callCount)); // no additional upstream call

        Assert.NotNull(result2);
        Assert.Contains("Docker-Content-Digest", ctx2.Response.Headers.Keys);
        Assert.Equal(digest, ctx2.Response.Headers["Docker-Content-Digest"].ToString());
    }

    [Fact]
    public async Task CacheDisabled_AlwaysForwards()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] content = "no-cache-body"u8.ToArray();
        string digest = ComputeSha256Digest(content);
        string path = $"library/nginx/blobs/{digest}";
        int callCount = 0;

        var upstreamHandler = new TestHttpMessageHandler(request =>
        {
            Interlocked.Increment(ref callCount);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            };
        });

        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(upstreamHandler)
            .WithCacheOptions(new CacheOptions { Enabled = false })
            .Build();

        await service.HandleAsync(path, CachingRegistryServiceBuilder.CreateHttpContext("GET", path), ct);
        await service.HandleAsync(path, CachingRegistryServiceBuilder.CreateHttpContext("GET", path), ct);

        Assert.Equal(2, Volatile.Read(ref callCount));
    }

    [Fact]
    public async Task TagReference_CachesManifest_WhenDockerContentDigestPresent()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] content = "tag-manifest-body"u8.ToArray();
        string digest = ComputeSha256Digest(content);
        string path = "library/nginx/manifests/latest";
        int callCount = 0;

        var upstreamHandler = new TestHttpMessageHandler(request =>
        {
            Interlocked.Increment(ref callCount);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/vnd.docker.distribution.manifest.v2+json") },
                },
            };
            response.Headers.TryAddWithoutValidation("Docker-Content-Digest", digest);
            return response;
        });

        FileSystemContentStore store = CreateStore();
        var keyedLock = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance, _metrics);

        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(upstreamHandler)
            .WithStore(store)
            .WithKeyedLock(keyedLock)
            .Build();

        await service.HandleAsync(path, CachingRegistryServiceBuilder.CreateHttpContext("GET", path), ct);
        await service.HandleAsync(path, CachingRegistryServiceBuilder.CreateHttpContext("GET", path), ct);

        Assert.Equal(1, Volatile.Read(ref callCount));
    }

    [Fact]
    public async Task TagReference_Passthrough_WithoutDockerContentDigest()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = "library/nginx/manifests/latest";
        int callCount = 0;

        var upstreamHandler = new TestHttpMessageHandler(request =>
        {
            Interlocked.Increment(ref callCount);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("tag-body"),
            };
        });

        FileSystemContentStore store = CreateStore();
        var keyedLock = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance, _metrics);

        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(upstreamHandler)
            .WithStore(store)
            .WithKeyedLock(keyedLock)
            .Build();

        await service.HandleAsync(path, CachingRegistryServiceBuilder.CreateHttpContext("GET", path), ct);
        await service.HandleAsync(path, CachingRegistryServiceBuilder.CreateHttpContext("GET", path), ct);

        Assert.Equal(2, Volatile.Read(ref callCount));
    }

    [Fact]
    public async Task HeadMiss_Passthrough()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] content = "head-miss"u8.ToArray();
        string digest = ComputeSha256Digest(content);
        string path = $"library/nginx/blobs/{digest}";
        int callCount = 0;

        var upstreamHandler = new TestHttpMessageHandler(request =>
        {
            Interlocked.Increment(ref callCount);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            };
        });

        FileSystemContentStore store = CreateStore();

        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(upstreamHandler)
            .WithStore(store)
            .Build();

        await service.HandleAsync(path, CachingRegistryServiceBuilder.CreateHttpContext("HEAD", path), ct);
        await service.HandleAsync(path, CachingRegistryServiceBuilder.CreateHttpContext("HEAD", path), ct);

        Assert.Equal(2, Volatile.Read(ref callCount));
    }

    [Fact]
    public async Task HeadHit_AnswersFromCache()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] content = "head-hit"u8.ToArray();
        string digest = ComputeSha256Digest(content);
        string path = $"library/nginx/blobs/{digest}";
        string cacheKey = DigestCacheKey.FromDigest(Digest.Parse(digest));

        // Populate cache first
        var fillHandler = new TestHttpMessageHandler(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain") },
                },
            });
        FileSystemContentStore store = CreateStore();
        var keyedLock = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance, _metrics);

        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(fillHandler)
            .WithStore(store)
            .WithKeyedLock(keyedLock)
            .Build();

        // Full GET to populate
        HttpContext getCtx = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, getCtx, ct);
        Assert.Equal(StatusCodes.Status200OK, getCtx.Response.StatusCode);

        // Verify the store actually has the cached content
        CachedContent? cached = await store.TryGetAsync(cacheKey, ct);
        Assert.NotNull(cached);
        Assert.Equal(content.Length, cached.Length);

        // HEAD request
        HttpContext headCtx = CachingRegistryServiceBuilder.CreateHttpContext("HEAD", path);
        await service.HandleAsync(path, headCtx, ct);

        Assert.Equal(StatusCodes.Status200OK, headCtx.Response.StatusCode);
        Assert.Equal("text/plain", headCtx.Response.Headers["Content-Type"].ToString());
        Assert.Equal(content.Length, headCtx.Response.ContentLength);
        Assert.Contains("Docker-Content-Digest", headCtx.Response.Headers.Keys);
    }

    [Fact]
    public async Task ConcurrentMisses_SameDigest_SingleUpstreamFetch()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] content = "concurrent-miss-single-flight"u8.ToArray();
        string digest = ComputeSha256Digest(content);
        string path = $"library/nginx/blobs/{digest}";

        // Gate lets us control exactly when the upstream fetch completes so we can
        // ensure the other requests are already waiting on the KeyedAsyncLock.
        var fetchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetchRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int callCount = 0;

        var handler = new AsyncTestHandler(async request =>
        {
            Interlocked.Increment(ref callCount);
            fetchStarted.TrySetResult();
            await fetchRelease.Task.WaitAsync(ct).ConfigureAwait(false);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            };
        });

        FileSystemContentStore store = CreateStore();
        var keyedLock = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance, _metrics);

        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(handler)
            .WithStore(store)
            .WithKeyedLock(keyedLock)
            .Build();

        // Start 4 concurrent requests for the same digest.
        Task<IResult>[] tasks = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() => service.HandleAsync(path, CachingRegistryServiceBuilder.CreateHttpContext("GET", path), ct), ct))
            .ToArray();

        // Wait for the first request to begin fetching upstream, then give the
        // remaining requests time to queue up behind the KeyedAsyncLock.
        await fetchStarted.Task.WaitAsync(ct);
        await Task.Delay(50, ct);

        // Release the upstream fetch; the first request commits to cache and
        // releases the lock so the other three see a cache hit.
        fetchRelease.SetResult();
        await Task.WhenAll(tasks);

        Assert.Equal(1, Volatile.Read(ref callCount));
    }

    [Fact]
    public async Task BlobRedirect_FollowsWithNoAuthHeader_AndCaches()
    {
        // Arrange: upstream returns 307 pointing at a CDN URL.
        // The CDN client must NOT receive an Authorization header (no token leak).
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] content = "blob-via-cdn-redirect"u8.ToArray();
        string digest = ComputeSha256Digest(content);
        string path = $"library/nginx/blobs/{digest}";

        var redirectLocation = new Uri("https://cdn.test.local/blob-presigned");

        var upstreamHandler = new TestHttpMessageHandler(_ =>
            new HttpResponseMessage(System.Net.HttpStatusCode.TemporaryRedirect)
            {
                Headers = { Location = redirectLocation },
            });

        // Capture the redirect request so we can assert on its headers.
        HttpRequestMessage? capturedRedirectRequest = null;
        var cdnHandler = new TestHttpMessageHandler(request =>
        {
            capturedRedirectRequest = request;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream") },
                },
            };
        });

        FileSystemContentStore store = CreateStore();
        var keyedLock = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance, _metrics);
        var httpClientFactory = new SingleClientFactory("upstream-redirect", new HttpClient(cdnHandler));

        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(upstreamHandler)
            .WithStore(store)
            .WithKeyedLock(keyedLock)
            .WithHttpClientFactory(httpClientFactory)
            .Build();

        HttpContext ctx = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, ctx, ct);

        // The CDN request must carry no Authorization header — bearer tokens must
        // never be forwarded to third-party CDN pre-signed URLs.
        Assert.NotNull(capturedRedirectRequest);
        Assert.False(
            capturedRedirectRequest.Headers.Contains("Authorization"),
            "Authorization header must not be sent to the redirect (CDN) target.");

        // Content must have been cached.
        string cacheKey = DigestCacheKey.FromDigest(DockerMirror.Caching.Digest.Parse(digest));
        CachedContent? cached = await store.TryGetAsync(cacheKey, ct);
        Assert.NotNull(cached);
        await cached.Stream.DisposeAsync();
    }

    [Fact]
    public async Task RangeGetMiss_PassthroughAndEnqueuesWarm()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] content = "range-miss-full-content"u8.ToArray();
        string digest = ComputeSha256Digest(content);
        string path = $"library/nginx/blobs/{digest}";

        // Upstream returns a partial response for the ranged fetch.
        var upstreamHandler = new TestHttpMessageHandler(_ =>
            new HttpResponseMessage(System.Net.HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(content[..5]),
            });

        FileSystemContentStore store = CreateStore();
        var warmQueue = new CacheWarmingQueue(Options.Create(new MirrorOptions { Cache = new CacheOptions { WarmQueueCapacity = 256 } }), _metrics);

        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(upstreamHandler)
            .WithStore(store)
            .WithWarmingQueue(warmQueue)
            .Build();

        HttpContext ctx = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        ctx.Request.Headers["Range"] = "bytes=0-4";
        await service.HandleAsync(path, ctx, ct);

        // The upstream partial response is relayed.
        Assert.Equal(StatusCodes.Status206PartialContent, ctx.Response.StatusCode);

        // A background warm request must have been enqueued for the full blob.
        Assert.True(warmQueue.Reader.TryRead(out WarmRequest warmRequest),
            "Expected a warm request to be enqueued after a range miss.");
        Assert.Equal(digest, warmRequest.Digest.Canonical);
        Assert.Equal("blobs", warmRequest.ResourceType);
    }

    [Fact]
    public async Task DigestMismatch_OnCommit_ClientReceivesBytesButCacheStaysEmpty()
    {
        // A corrupted upstream body produces a hash that doesn't match the requested
        // digest. The client still receives the (bad) bytes via the tee, but the
        // cache entry must not be promoted.
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] corruptContent = "corrupted-body-data"u8.ToArray();
        // Use the digest of DIFFERENT bytes so the hash will never match.
        string realDigest = ComputeSha256Digest("the-real-content"u8.ToArray());
        string path = $"library/nginx/blobs/{realDigest}";

        var upstreamHandler = new TestHttpMessageHandler(_ =>
            new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(corruptContent),
            });

        FileSystemContentStore store = CreateStore();
        var keyedLock = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance, _metrics);

        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(upstreamHandler)
            .WithStore(store)
            .WithKeyedLock(keyedLock)
            .Build();

        HttpContext ctx = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, ctx, ct);

        // Client received the response (200 was set before the tee).
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);

        // Cache must be empty: the digest mismatch prevented promotion.
        string cacheKey = DigestCacheKey.FromDigest(DockerMirror.Caching.Digest.Parse(realDigest));
        CachedContent? cached = await store.TryGetAsync(cacheKey, ct);
        Assert.Null(cached);
    }

    [Fact]
    public async Task TagReference_StalePointer_HeadRevalidateUnchanged_ServesFromCache()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] content = "stale-revalidate-body"u8.ToArray();
        string digest = ComputeSha256Digest(content);
        string path = "library/nginx/manifests/latest";
        var tagTtl = TimeSpan.FromMinutes(5);
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);

        int callCount = 0;
        int headCount = 0;

        var upstreamHandler = new TestHttpMessageHandler(request =>
        {
            Interlocked.Increment(ref callCount);

            if (request.Method == HttpMethod.Head)
            {
                Interlocked.Increment(ref headCount);
                var headRsp = new HttpResponseMessage(HttpStatusCode.OK);
                headRsp.Headers.TryAddWithoutValidation("Docker-Content-Digest", digest);
                return headRsp;
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/vnd.docker.distribution.manifest.v2+json") },
                },
            };
            response.Headers.TryAddWithoutValidation("Docker-Content-Digest", digest);
            return response;
        });

        FileSystemContentStore store = CreateStore();
        var keyedLock = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance, _metrics);

        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(upstreamHandler)
            .WithStore(store)
            .WithKeyedLock(keyedLock)
            .WithTimeProvider(fakeTime)
            .WithCacheOptions(new CacheOptions { Enabled = true, TagManifests = new TagManifestOptions { Ttl = tagTtl, ConditionalRevalidation = false } })
            .Build();

        // First request: caches body and pointer
        HttpContext ctx1 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, ctx1, ct);
        Assert.Equal(StatusCodes.Status200OK, ctx1.Response.StatusCode);
        Assert.Equal(1, Volatile.Read(ref callCount));

        // Advance time past TTL
        fakeTime.Advance(tagTtl + TimeSpan.FromMinutes(1));

        // Second request: stale pointer → HEAD revalidate → unchanged → serve from cache
        HttpContext ctx2 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, ctx2, ct);
        Assert.Equal(StatusCodes.Status200OK, ctx2.Response.StatusCode);
        Assert.Contains("Docker-Content-Digest", ctx2.Response.Headers.Keys);

        // 1 initial GET + 1 HEAD revalidate = 2 total upstream calls
        Assert.Equal(2, Volatile.Read(ref callCount));
        Assert.Equal(1, Volatile.Read(ref headCount));
    }

    [Fact]
    public async Task TagReference_StalePointer_DigestChanged_Refetches()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] content1 = "old-manifest-body"u8.ToArray();
        string digest1 = ComputeSha256Digest(content1);
        byte[] content2 = "new-manifest-body"u8.ToArray();
        string digest2 = ComputeSha256Digest(content2);
        string path = "library/nginx/manifests/latest";
        var tagTtl = TimeSpan.FromMinutes(5);
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);

        int callCount = 0;
        int phase = 0;

        var upstreamHandler = new TestHttpMessageHandler(request =>
        {
            Interlocked.Increment(ref callCount);

            if (request.Method == HttpMethod.Head)
            {
                // HEAD returns the new digest
                var headRsp = new HttpResponseMessage(HttpStatusCode.OK);
                headRsp.Headers.TryAddWithoutValidation("Docker-Content-Digest", digest2);
                return headRsp;
            }

            // GET: first call returns old content, subsequent call returns new
            int round = Interlocked.Increment(ref phase);
            byte[] content = round == 1 ? content1 : content2;
            string digest = round == 1 ? digest1 : digest2;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/vnd.docker.distribution.manifest.v2+json") },
                },
            };
            response.Headers.TryAddWithoutValidation("Docker-Content-Digest", digest);
            return response;
        });

        FileSystemContentStore store = CreateStore();
        var keyedLock = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance, _metrics);

        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(upstreamHandler)
            .WithStore(store)
            .WithKeyedLock(keyedLock)
            .WithTimeProvider(fakeTime)
            .WithCacheOptions(new CacheOptions { Enabled = true, TagManifests = new TagManifestOptions { Ttl = tagTtl, ConditionalRevalidation = false } })
            .Build();

        // First request: caches body1 and pointer(digest1)
        HttpContext ctx1 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, ctx1, ct);
        Assert.Equal(StatusCodes.Status200OK, ctx1.Response.StatusCode);

        // Advance time past TTL
        fakeTime.Advance(tagTtl + TimeSpan.FromMinutes(1));

        // Second request: stale → HEAD returns digest2 → refetch body2
        HttpContext ctx2 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, ctx2, ct);
        Assert.Equal(StatusCodes.Status200OK, ctx2.Response.StatusCode);
        Assert.Equal(digest2, ctx2.Response.Headers["Docker-Content-Digest"].ToString());

        // 1 initial GET + 1 HEAD + 1 refetch GET = 3 total
        Assert.Equal(3, Volatile.Read(ref callCount));
    }

    [Fact]
    public async Task TagReference_HeadServedFromCache()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] content = "head-tag-hit-body"u8.ToArray();
        string digest = ComputeSha256Digest(content);
        string path = "library/nginx/manifests/latest";

        int callCount = 0;
        var upstreamHandler = new TestHttpMessageHandler(request =>
        {
            Interlocked.Increment(ref callCount);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/vnd.docker.distribution.manifest.v2+json") },
                },
            };
            response.Headers.TryAddWithoutValidation("Docker-Content-Digest", digest);
            return response;
        });

        FileSystemContentStore store = CreateStore();
        var keyedLock = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance, _metrics);

        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(upstreamHandler)
            .WithStore(store)
            .WithKeyedLock(keyedLock)
            .Build();

        // First: GET to populate
        HttpContext getCtx = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, getCtx, ct);
        Assert.Equal(StatusCodes.Status200OK, getCtx.Response.StatusCode);
        Assert.Equal(1, Volatile.Read(ref callCount));

        // Second: HEAD → served from cache
        HttpContext headCtx = CachingRegistryServiceBuilder.CreateHttpContext("HEAD", path);
        await service.HandleAsync(path, headCtx, ct);
        Assert.Equal(StatusCodes.Status200OK, headCtx.Response.StatusCode);
        Assert.Equal("application/vnd.docker.distribution.manifest.v2+json", headCtx.Response.Headers["Content-Type"].ToString());
        Assert.Equal(content.Length, headCtx.Response.ContentLength);
        Assert.Contains("Docker-Content-Digest", headCtx.Response.Headers.Keys);
        Assert.Equal(1, Volatile.Read(ref callCount)); // No additional upstream call
    }

    [Fact]
    public async Task TagReference_HeadMiss_CreatesPointer()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string digest = $"sha256:{Convert.ToHexStringLower(SHA256.HashData("head-miss-pointer-body"u8))}";
        string path = "library/nginx/manifests/latest";
        int headCount = 0;

        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        DateTimeOffset start = fakeTime.GetUtcNow();

        var upstreamHandler = new TestHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Head)
            {
                Interlocked.Increment(ref headCount);
                var headRsp = new HttpResponseMessage(HttpStatusCode.OK);
                headRsp.Headers.TryAddWithoutValidation("Docker-Content-Digest", digest);
                headRsp.Content = new StringContent(string.Empty);
                headRsp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/vnd.docker.distribution.manifest.v2+json");
                return headRsp;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        FileSystemContentStore store = CreateStore();

        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(upstreamHandler)
            .WithStore(store)
            .WithTimeProvider(fakeTime)
            .Build();

        // HEAD tag → creates pointer (no cached body)
        HttpContext headCtx = CachingRegistryServiceBuilder.CreateHttpContext("HEAD", path);
        await service.HandleAsync(path, headCtx, ct);
        Assert.Equal(StatusCodes.Status200OK, headCtx.Response.StatusCode);
        Assert.Equal(1, Volatile.Read(ref headCount));

        // Verify the pointer was stored
        string rewrittenName = "library/nginx";
        string tagKey = TagCacheKey.From(rewrittenName, "latest", acceptKey: null);
        TagPointer? pointer = await store.TryGetTagAsync(tagKey, ct);
        Assert.NotNull(pointer);
        Assert.Equal(digest, pointer.Value.Digest.Canonical);
        // The pointer store serialises ResolvedAtUtc as Unix milliseconds, so the stored
        // value may lose sub-millisecond precision from the FakeTimeProvider.
        TimeSpan delta = (pointer.Value.ResolvedAtUtc - start).Duration();
        Assert.True(delta < TimeSpan.FromMilliseconds(1),
            $"Expected ResolvedAtUtc within 1 ms of {start:O}, got {pointer.Value.ResolvedAtUtc:O} (delta {delta.TotalMilliseconds:F3} ms)");
    }

    [Fact]
    public async Task CacheTagManifestsFalse_TagBypasses()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = "library/nginx/manifests/latest";
        int callCount = 0;

        var upstreamHandler = new TestHttpMessageHandler(request =>
        {
            Interlocked.Increment(ref callCount);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("tag-body"),
            };
        });

        FileSystemContentStore store = CreateStore();

        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(upstreamHandler)
            .WithStore(store)
            .WithCacheOptions(new CacheOptions { Enabled = true, TagManifests = new TagManifestOptions { Enabled = false } })
            .Build();

        await service.HandleAsync(path, CachingRegistryServiceBuilder.CreateHttpContext("GET", path), ct);
        await service.HandleAsync(path, CachingRegistryServiceBuilder.CreateHttpContext("GET", path), ct);

        Assert.Equal(2, Volatile.Read(ref callCount));
    }

    // ---- HTTP protocol cache header tests ----

    [Fact]
    public async Task Digest_IfNoneMatch_Returns304()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData("test-content"u8));
        string path = $"library/nginx/blobs/{digest}";
        int callCount = 0;

        var upstreamHandler = new TestHttpMessageHandler(_ =>
        {
            Interlocked.Increment(ref callCount);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(upstreamHandler)
            .WithStore(CreateStore())
            .Build();

        HttpContext ctx = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        ctx.Request.Headers.IfNoneMatch = $"\"{digest}\"";

        await service.HandleAsync(path, ctx, ct);

        Assert.Equal(StatusCodes.Status304NotModified, ctx.Response.StatusCode);
        Assert.Equal($"\"{digest}\"", ctx.Response.Headers.ETag.ToString());
        Assert.Equal(digest, ctx.Response.Headers["Docker-Content-Digest"].ToString());
        Assert.Contains("immutable", ctx.Response.Headers.CacheControl.ToString());
        Assert.Equal("0", ctx.Response.Headers.Age.ToString());
        Assert.Equal(0, Volatile.Read(ref callCount));
    }

    [Fact]
    public async Task Digest_IfNoneMatch_Mismatch_ProceedsToUpstream()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] content = "upstream-body"u8.ToArray();
        string digest = ComputeSha256Digest(content);
        string path = $"library/nginx/blobs/{digest}";
        int callCount = 0;

        var upstreamHandler = new TestHttpMessageHandler(request =>
        {
            Interlocked.Increment(ref callCount);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            };
        });

        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(upstreamHandler)
            .WithStore(CreateStore())
            .Build();

        HttpContext ctx = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        ctx.Request.Headers.IfNoneMatch = "\"sha256:different\"";

        await service.HandleAsync(path, ctx, ct);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.Equal(1, Volatile.Read(ref callCount));
    }

    [Fact]
    public async Task Digest_CacheHit_IncludesETagAndCacheControlImmutable()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] content = "cached-content"u8.ToArray();
        string digest = ComputeSha256Digest(content);
        string path = $"library/nginx/blobs/{digest}";
        int callCount = 0;

        var upstreamHandler = new TestHttpMessageHandler(request =>
        {
            Interlocked.Increment(ref callCount);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            };
        });

        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(upstreamHandler)
            .WithStore(CreateStore())
            .Build();

        // First: miss → cache
        HttpContext ctx1 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, ctx1, ct);
        Assert.Equal(StatusCodes.Status200OK, ctx1.Response.StatusCode);

        // Second: cache hit
        HttpContext ctx2 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, ctx2, ct);

        Assert.Equal(StatusCodes.Status200OK, ctx2.Response.StatusCode);
        Assert.Equal($"\"{digest}\"", ctx2.Response.Headers.ETag.ToString());
        Assert.Contains("immutable", ctx2.Response.Headers.CacheControl.ToString());
        Assert.True(ctx2.Response.Headers.ContainsKey("Age"));
        Assert.Equal(1, Volatile.Read(ref callCount));
    }

    [Fact]
    public async Task Digest_Head_IfNoneMatch_Returns304()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData("head-test"u8));
        string path = $"library/nginx/blobs/{digest}";

        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(new TestHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.NotFound)))
            .WithStore(CreateStore())
            .Build();

        HttpContext ctx = CachingRegistryServiceBuilder.CreateHttpContext("HEAD", path);
        ctx.Request.Headers.IfNoneMatch = $"\"{digest}\"";

        await service.HandleAsync(path, ctx, ct);

        Assert.Equal(StatusCodes.Status304NotModified, ctx.Response.StatusCode);
        Assert.Equal($"\"{digest}\"", ctx.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task Tag_IfNoneMatch_FreshPointer_Returns304()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] content = "tag-body"u8.ToArray();
        string digest = ComputeSha256Digest(content);
        string path = "library/nginx/manifests/latest";
        int callCount = 0;

        var upstreamHandler = new TestHttpMessageHandler(request =>
        {
            Interlocked.Increment(ref callCount);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/vnd.docker.distribution.manifest.v2+json") },
                },
            };
            response.Headers.TryAddWithoutValidation("Docker-Content-Digest", digest);
            return response;
        });

        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(upstreamHandler)
            .WithStore(CreateStore())
            .WithCacheOptions(new CacheOptions { Enabled = true, TagManifests = new TagManifestOptions { Ttl = TimeSpan.FromMinutes(5), ConditionalRevalidation = true } })
            .Build();

        // First: miss → cache body and pointer
        HttpContext ctx1 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, ctx1, ct);
        Assert.Equal(StatusCodes.Status200OK, ctx1.Response.StatusCode);

        // Second: fresh pointer + If-None-Match → 304
        HttpContext ctx2 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        ctx2.Request.Headers.IfNoneMatch = $"\"{digest}\"";
        await service.HandleAsync(path, ctx2, ct);

        Assert.Equal(StatusCodes.Status304NotModified, ctx2.Response.StatusCode);
        Assert.Equal($"\"{digest}\"", ctx2.Response.Headers.ETag.ToString());
        Assert.DoesNotContain("immutable", ctx2.Response.Headers.CacheControl.ToString());
        Assert.Equal(1, Volatile.Read(ref callCount));
    }

    [Fact]
    public async Task Tag_ConditionalRevalidation_Upstream304_RefreshesPointer()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] content = "stable-body"u8.ToArray();
        string digest = ComputeSha256Digest(content);
        string path = "library/nginx/manifests/stable";
        var tagTtl = TimeSpan.FromMilliseconds(100);
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);

        int callCount = 0;
        int getCount = 0;

        var upstreamHandler = new TestHttpMessageHandler(request =>
        {
            Interlocked.Increment(ref callCount);

            if (request.Headers.IfNoneMatch.Count > 0)
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }

            Interlocked.Increment(ref getCount);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/vnd.docker.distribution.manifest.v2+json") },
                },
            };
            response.Headers.TryAddWithoutValidation("Docker-Content-Digest", digest);
            return response;
        });

        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(upstreamHandler)
            .WithStore(CreateStore())
            .WithTimeProvider(fakeTime)
            .WithCacheOptions(new CacheOptions { Enabled = true, TagManifests = new TagManifestOptions { Ttl = tagTtl, ConditionalRevalidation = true } })
            .Build();

        // First: miss → cache body and pointer
        HttpContext ctx1 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, ctx1, ct);
        Assert.Equal(StatusCodes.Status200OK, ctx1.Response.StatusCode);
        Assert.Equal(1, Volatile.Read(ref getCount));

        // Advance time past TTL so pointer is stale
        fakeTime.Advance(tagTtl + TimeSpan.FromMilliseconds(50));

        // Second: stale pointer, body cached → conditional GET → upstream 304 → serve cached
        HttpContext ctx2 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, ctx2, ct);

        Assert.Equal(StatusCodes.Status200OK, ctx2.Response.StatusCode);
        Assert.Equal(1, Volatile.Read(ref getCount));
    }

    [Fact]
    public async Task Tag_ConditionalRevalidation_Upstream304_ClientIfNoneMatch_Returns304()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] content = "stable-body"u8.ToArray();
        string digest = ComputeSha256Digest(content);
        string path = "library/nginx/manifests/v1";
        var tagTtl = TimeSpan.FromMilliseconds(100);
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);

        int getCount = 0;

        var upstreamHandler = new TestHttpMessageHandler(request =>
        {
            if (request.Headers.IfNoneMatch.Count > 0)
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }

            Interlocked.Increment(ref getCount);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/vnd.docker.distribution.manifest.v2+json") },
                },
            };
            response.Headers.TryAddWithoutValidation("Docker-Content-Digest", digest);
            return response;
        });

        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(upstreamHandler)
            .WithStore(CreateStore())
            .WithTimeProvider(fakeTime)
            .WithCacheOptions(new CacheOptions { Enabled = true, TagManifests = new TagManifestOptions { Ttl = tagTtl, ConditionalRevalidation = true } })
            .Build();

        // First request: caches body and pointer
        HttpContext ctx1 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, ctx1, ct);
        Assert.Equal(StatusCodes.Status200OK, ctx1.Response.StatusCode);
        Assert.Equal(1, Volatile.Read(ref getCount));

        fakeTime.Advance(tagTtl + TimeSpan.FromMilliseconds(50));

        // Second: stale + client has If-None-Match → upstream 304 → 304 to client
        HttpContext ctx2 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        ctx2.Request.Headers.IfNoneMatch = $"\"{digest}\"";
        await service.HandleAsync(path, ctx2, ct);

        Assert.Equal(StatusCodes.Status304NotModified, ctx2.Response.StatusCode);
        Assert.Equal($"\"{digest}\"", ctx2.Response.Headers.ETag.ToString());
        Assert.Equal(1, Volatile.Read(ref getCount));
    }

    [Fact]
    public async Task Tag_ConditionalRevalidation_Upstream200_Recaches()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] oldContent = "old-body"u8.ToArray();
        string oldDigest = ComputeSha256Digest(oldContent);
        byte[] newContent = "new-body"u8.ToArray();
        string newDigest = ComputeSha256Digest(newContent);
        string path = "library/nginx/manifests/changing";
        var tagTtl = TimeSpan.FromMilliseconds(100);
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);

        int getCount = 0;

        var upstreamHandler = new TestHttpMessageHandler(request =>
        {
            int current = Interlocked.Increment(ref getCount);
            byte[] content = current == 1 ? oldContent : newContent;
            string digest = current == 1 ? oldDigest : newDigest;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/vnd.docker.distribution.manifest.v2+json") },
                },
            };
            response.Headers.TryAddWithoutValidation("Docker-Content-Digest", digest);
            return response;
        });

        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(upstreamHandler)
            .WithStore(CreateStore())
            .WithTimeProvider(fakeTime)
            .WithCacheOptions(new CacheOptions { Enabled = true, TagManifests = new TagManifestOptions { Ttl = tagTtl, ConditionalRevalidation = true } })
            .Build();

        // First: caches old body
        HttpContext ctx1 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, ctx1, ct);
        Assert.Equal(StatusCodes.Status200OK, ctx1.Response.StatusCode);
        Assert.Equal(1, Volatile.Read(ref getCount));

        fakeTime.Advance(tagTtl + TimeSpan.FromMilliseconds(50));

        // Second: stale → conditional GET → upstream 200 with new digest → re-cached
        HttpContext ctx2 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, ctx2, ct);

        Assert.Equal(StatusCodes.Status200OK, ctx2.Response.StatusCode);
        Assert.Equal(2, Volatile.Read(ref getCount));
        Assert.Equal(newDigest, ctx2.Response.Headers["Docker-Content-Digest"].ToString());
    }

    [Fact]
    public async Task Tag_ConditionalRevalidation_BodyEvicted_FallsBackToHead()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] content = "evicted-body"u8.ToArray();
        string digest = ComputeSha256Digest(content);
        string path = "library/nginx/manifests/evicted";
        var tagTtl = TimeSpan.FromMilliseconds(100);
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);

        int getCount = 0;
        int headCount = 0;

        var upstreamHandler = new TestHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Head)
            {
                Interlocked.Increment(ref headCount);
                var head = new HttpResponseMessage(HttpStatusCode.OK);
                head.Headers.TryAddWithoutValidation("Docker-Content-Digest", digest);
                head.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("application/vnd.docker.distribution.manifest.v2+json");
                return head;
            }

            Interlocked.Increment(ref getCount);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/vnd.docker.distribution.manifest.v2+json") },
                },
            };
            response.Headers.TryAddWithoutValidation("Docker-Content-Digest", digest);
            return response;
        });

        FileSystemContentStore store = CreateStore();
        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(upstreamHandler)
            .WithStore(store)
            .WithTimeProvider(fakeTime)
            .WithCacheOptions(new CacheOptions { Enabled = true, TagManifests = new TagManifestOptions { Ttl = tagTtl, ConditionalRevalidation = true } })
            .Build();

        // First: miss → cache body and pointer
        HttpContext ctx1 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, ctx1, ct);
        Assert.Equal(StatusCodes.Status200OK, ctx1.Response.StatusCode);
        Assert.Equal(1, Volatile.Read(ref getCount));

        // Advance time past TTL so pointer is stale.
        fakeTime.Advance(tagTtl + TimeSpan.FromMilliseconds(50));

        // Build a new service where the store reports the body as evicted.
        // The original store (FileSystemContentStore) is reused as the tag-pointer
        // store so the pointer from the first request is still visible.
        var evictedStore = new EvictedBodyStore(store);
        CachingRegistryService service2 = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(upstreamHandler)
            .WithStore(evictedStore)
            .WithTagStore(store)
            .WithTimeProvider(fakeTime)
            .WithCacheOptions(new CacheOptions { Enabled = true, TagManifests = new TagManifestOptions { Ttl = tagTtl, ConditionalRevalidation = true } })
            .Build();

        // Second: stale pointer, body evicted → falls back to HEAD
        HttpContext ctx2 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service2.HandleAsync(path, ctx2, ct);

        // Body was evicted so the HEAD path kicks in, which sees the digest is unchanged,
        // serves the body from cache via TryServeCachedAsync (which delegates to the real store).
        // Since the evictedStore only fakes ExistsAsync, TryGetAsync still returns the body.
        Assert.Equal(StatusCodes.Status200OK, ctx2.Response.StatusCode);
        // The conditional revalidation path was NOT taken (no conditional GET with If-None-Match).
        // Instead the HEAD fallback was used, which re-discovered the same digest.
        Assert.Equal(1, Volatile.Read(ref headCount));
    }

    [Fact]
    public async Task Tag_FullGet304_CachesBodyForNextRequest()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] content = "full-get-304-body"u8.ToArray();
        string digest = ComputeSha256Digest(content);
        string path = "library/nginx/manifests/v2";
        int callCount = 0;

        var upstreamHandler = new TestHttpMessageHandler(request =>
        {
            Interlocked.Increment(ref callCount);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/vnd.docker.distribution.manifest.v2+json") },
                },
            };
            response.Headers.TryAddWithoutValidation("Docker-Content-Digest", digest);
            return response;
        });

        FileSystemContentStore store = CreateStore();
        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(upstreamHandler)
            .WithStore(store)
            .WithCacheOptions(new CacheOptions { Enabled = true, TagManifests = new TagManifestOptions { Ttl = TimeSpan.FromMinutes(5), ConditionalRevalidation = true } })
            .Build();

        // First: miss (no pointer) → full GET upstream, client sends If-None-Match with
        // the expected digest before the response is known. Since it matches, we should
        // get 304 and the body should still populate the cache.
        HttpContext ctx1 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        ctx1.Request.Headers.IfNoneMatch = $"\"{digest}\"";
        await service.HandleAsync(path, ctx1, ct);

        Assert.Equal(StatusCodes.Status304NotModified, ctx1.Response.StatusCode);
        Assert.Equal(1, Volatile.Read(ref callCount));

        // Second: fresh pointer should serve from cache without calling upstream.
        HttpContext ctx2 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, ctx2, ct);

        Assert.Equal(StatusCodes.Status200OK, ctx2.Response.StatusCode);
        Assert.Equal(1, Volatile.Read(ref callCount)); // No additional upstream call

        // Verify the body was actually persisted to the content store.
        var digestObj = Digest.Parse(digest);
        string cacheKey = DigestCacheKey.FromDigest(digestObj);
        CachedContent? cached = await store.TryGetAsync(cacheKey, ct);
        Assert.NotNull(cached);
        using var ms = new MemoryStream();
        await cached.Stream.CopyToAsync(ms, ct);
        Assert.Equal(content, ms.ToArray());
    }

    [Fact]
    public async Task Digest_HeadersDisabled_NoETagOrCacheControl()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] content = "no-headers-body"u8.ToArray();
        string digest = ComputeSha256Digest(content);
        string path = $"library/nginx/blobs/{digest}";
        int callCount = 0;

        var upstreamHandler = new TestHttpMessageHandler(request =>
        {
            Interlocked.Increment(ref callCount);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            };
        });

        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(upstreamHandler)
            .WithStore(CreateStore())
            .WithCacheOptions(new CacheOptions { Enabled = true, Headers = new CacheHeaderOptions { Enabled = false } })
            .Build();

        // First: miss → cache
        HttpContext ctx1 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, ctx1, ct);
        Assert.Equal(StatusCodes.Status200OK, ctx1.Response.StatusCode);

        // Second: cache hit → no ETag/Cache-Control synthetic headers
        HttpContext ctx2 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, ctx2, ct);

        Assert.Equal(StatusCodes.Status200OK, ctx2.Response.StatusCode);
        Assert.False(ctx2.Response.Headers.ContainsKey("ETag"));
        Assert.False(ctx2.Response.Headers.ContainsKey("Cache-Control"));
    }

    // ---- List response cache tests ----

    [Fact]
    public async Task TagsList_CachesAndServesFromCache()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = "library/nginx/tags/list";
        byte[] tagsBody = "{\"name\":\"nginx\",\"tags\":[\"latest\",\"1.0\"]}"u8.ToArray();
        int callCount = 0;

        var upstreamHandler = new TestHttpMessageHandler(request =>
        {
            Interlocked.Increment(ref callCount);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(tagsBody)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") },
                },
            };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"tags-etag\"");
            return response;
        });

        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(upstreamHandler)
            .WithStore(CreateStore())
            .WithCacheOptions(new CacheOptions { Enabled = true, TagsList = new ListCacheOptions { Enabled = true, Ttl = TimeSpan.FromMinutes(1) } })
            .Build();

        // First request: miss → fetch and cache
        HttpContext ctx1 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, ctx1, ct);
        Assert.Equal(StatusCodes.Status200OK, ctx1.Response.StatusCode);

        // Second request: cache hit
        HttpContext ctx2 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, ctx2, ct);
        Assert.Equal(StatusCodes.Status200OK, ctx2.Response.StatusCode);
        Assert.Equal(1, Volatile.Read(ref callCount)); // Only one upstream call
    }

    [Fact]
    public async Task TagsList_Non200_Passthrough_NotCached()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = "library/nginx/tags/list";
        int callCount = 0;

        var upstreamHandler = new TestHttpMessageHandler(request =>
        {
            Interlocked.Increment(ref callCount);
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });

        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(upstreamHandler)
            .WithStore(CreateStore())
            .WithCacheOptions(new CacheOptions { Enabled = true, TagsList = new ListCacheOptions { Enabled = true, Ttl = TimeSpan.FromMinutes(1) } })
            .Build();

        // First: non-200 → passthrough
        HttpContext ctx1 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, ctx1, ct);
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx1.Response.StatusCode);

        // Second: should still go to upstream (not cached)
        HttpContext ctx2 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, ctx2, ct);
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx2.Response.StatusCode);
        Assert.Equal(2, Volatile.Read(ref callCount));
    }

    [Fact]
    public async Task TagsList_IfNoneMatch_Returns304()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = "library/nginx/tags/list";
        byte[] tagsBody = "{\"name\":\"nginx\",\"tags\":[\"latest\"]}"u8.ToArray();
        int callCount = 0;

        var upstreamHandler = new TestHttpMessageHandler(request =>
        {
            Interlocked.Increment(ref callCount);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(tagsBody)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") },
                },
            };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"my-etag\"");
            return response;
        });

        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(upstreamHandler)
            .WithStore(CreateStore())
            .WithCacheOptions(new CacheOptions { Enabled = true, TagsList = new ListCacheOptions { Enabled = true, Ttl = TimeSpan.FromMinutes(1) } })
            .Build();

        // First: miss → cache
        HttpContext ctx1 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, ctx1, ct);
        Assert.Equal(StatusCodes.Status200OK, ctx1.Response.StatusCode);

        // Second: If-None-Match → 304
        HttpContext ctx2 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        ctx2.Request.Headers.IfNoneMatch = "\"my-etag\"";
        await service.HandleAsync(path, ctx2, ct);
        Assert.Equal(StatusCodes.Status304NotModified, ctx2.Response.StatusCode);
        Assert.Equal(1, Volatile.Read(ref callCount));
    }

    [Fact]
    public async Task TagsList_Expired_Revalidated_WithStaleETag()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = "library/nginx/tags/list";
        byte[] tagsBody = "{\"name\":\"nginx\",\"tags\":[\"latest\"]}"u8.ToArray();
        var tagTtl = TimeSpan.FromMilliseconds(100);
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);

        int callCount = 0;
        int fullFetchCount = 0;

        var upstreamHandler = new TestHttpMessageHandler(request =>
        {
            Interlocked.Increment(ref callCount);

            if (request.Headers.IfNoneMatch.Count > 0)
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }

            Interlocked.Increment(ref fullFetchCount);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(tagsBody)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") },
                },
            };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"stale-etag\"");
            return response;
        });

        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(upstreamHandler)
            .WithStore(CreateStore())
            .WithTimeProvider(fakeTime)
            .WithCacheOptions(new CacheOptions { Enabled = true, TagsList = new ListCacheOptions { Enabled = true, Ttl = tagTtl } })
            .Build();

        // First: miss → cache
        HttpContext ctx1 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, ctx1, ct);
        Assert.Equal(StatusCodes.Status200OK, ctx1.Response.StatusCode);
        Assert.Equal(1, Volatile.Read(ref fullFetchCount));
        Assert.Equal(1, Volatile.Read(ref callCount));

        fakeTime.Advance(tagTtl + TimeSpan.FromMilliseconds(50));

        // Second: stale → upstream conditional GET → 304 → serve cached
        HttpContext ctx2 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, ctx2, ct);
        Assert.Equal(StatusCodes.Status200OK, ctx2.Response.StatusCode);
        Assert.Equal(1, Volatile.Read(ref fullFetchCount)); // No additional full fetch
        Assert.Equal(2, Volatile.Read(ref callCount)); // Conditional GET happened
    }

    [Fact]
    public async Task TagsList_OversizedBody_Passthrough_NotCached()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = "library/nginx/tags/list";
        byte[] tagsBody = "{\"name\":\"nginx\",\"tags\":[\"latest\",\"1.0\",\"2.0\",\"3.0\"]}"u8.ToArray();
        int callCount = 0;

        var upstreamHandler = new TestHttpMessageHandler(request =>
        {
            Interlocked.Increment(ref callCount);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(tagsBody)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") },
                },
            };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"oversized-etag\"");
            return response;
        });

        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(upstreamHandler)
            .WithStore(CreateStore())
            .WithCacheOptions(new CacheOptions { Enabled = true, TagsList = new ListCacheOptions { Enabled = true, Ttl = TimeSpan.FromMinutes(1), MaxBodyBytes = 10 } })
            .Build();

        // First: body > MaxBodyBytes → served but not cached
        HttpContext ctx1 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, ctx1, ct);
        Assert.Equal(StatusCodes.Status200OK, ctx1.Response.StatusCode);
        ctx1.Response.Body.Position = 0;
        string body1 = await new StreamReader(ctx1.Response.Body).ReadToEndAsync(ct);
        Assert.Contains("latest", body1);

        // Second: must hit upstream again — body was not cached
        HttpContext ctx2 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, ctx2, ct);
        Assert.Equal(StatusCodes.Status200OK, ctx2.Response.StatusCode);
        Assert.Equal(2, Volatile.Read(ref callCount));
    }

    [Fact]
    public async Task TagsList_LinkHeader_PreservedOnCacheHit()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = "library/nginx/tags/list";
        byte[] tagsBody = "{\"name\":\"nginx\",\"tags\":[\"latest\"]}"u8.ToArray();
        int callCount = 0;

        var upstreamHandler = new TestHttpMessageHandler(request =>
        {
            Interlocked.Increment(ref callCount);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(tagsBody)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") },
                },
            };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"link-etag\"");
            response.Headers.TryAddWithoutValidation("Link", "</v2/nginx/tags/list?n=2&last=v1>; rel=\"next\"");
            return response;
        });

        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(upstreamHandler)
            .WithStore(CreateStore())
            .WithCacheOptions(new CacheOptions { Enabled = true, TagsList = new ListCacheOptions { Enabled = true, Ttl = TimeSpan.FromMinutes(1) } })
            .Build();

        // First: miss → cache
        HttpContext ctx1 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, ctx1, ct);
        Assert.Equal(StatusCodes.Status200OK, ctx1.Response.StatusCode);
        Assert.Equal("</v2/nginx/tags/list?n=2&last=v1>; rel=\"next\"", ctx1.Response.Headers["Link"].ToString());

        // Second: cache hit → Link header preserved
        HttpContext ctx2 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, ctx2, ct);
        Assert.Equal(StatusCodes.Status200OK, ctx2.Response.StatusCode);
        Assert.Equal("</v2/nginx/tags/list?n=2&last=v1>; rel=\"next\"", ctx2.Response.Headers["Link"].ToString());
        Assert.Equal(1, Volatile.Read(ref callCount));
    }

    [Fact]
    public async Task TagsList_CacheHit_SetsContentLength()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = "library/nginx/tags/list";
        byte[] tagsBody = "{\"name\":\"nginx\",\"tags\":[\"latest\",\"1.0\"]}"u8.ToArray();

        var upstreamHandler = new TestHttpMessageHandler(request =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(tagsBody)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") },
                },
            };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"cl-etag\"");
            return response;
        });

        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(upstreamHandler)
            .WithStore(CreateStore())
            .WithCacheOptions(new CacheOptions { Enabled = true, TagsList = new ListCacheOptions { Enabled = true, Ttl = TimeSpan.FromMinutes(1) } })
            .Build();

        // First: miss → fetch and cache.
        HttpContext ctx1 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, ctx1, ct);
        Assert.Equal(StatusCodes.Status200OK, ctx1.Response.StatusCode);
        Assert.Equal(tagsBody.Length, ctx1.Response.ContentLength);

        // Second: cache hit → Content-Length still set from the cached buffer.
        HttpContext ctx2 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, ctx2, ct);
        Assert.Equal(StatusCodes.Status200OK, ctx2.Response.StatusCode);
        Assert.Equal(tagsBody.Length, ctx2.Response.ContentLength);
    }

    [Fact]
    public async Task TagsList_Head_CacheHit_SetsContentLengthAndContentType()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = "library/nginx/tags/list";
        byte[] tagsBody = "{\"name\":\"nginx\",\"tags\":[\"latest\"]}"u8.ToArray();
        int callCount = 0;

        var upstreamHandler = new TestHttpMessageHandler(request =>
        {
            Interlocked.Increment(ref callCount);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(tagsBody)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") },
                },
            };
        });

        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(upstreamHandler)
            .WithStore(CreateStore())
            .WithCacheOptions(new CacheOptions { Enabled = true, TagsList = new ListCacheOptions { Enabled = true, Ttl = TimeSpan.FromMinutes(1) } })
            .Build();

        // Prime the cache with a GET.
        HttpContext getCtx = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, getCtx, ct);
        Assert.Equal(StatusCodes.Status200OK, getCtx.Response.StatusCode);

        // HEAD served from the fresh cache entry (no upstream call), with metadata headers.
        HttpContext headCtx = CachingRegistryServiceBuilder.CreateHttpContext("HEAD", path);
        await service.HandleAsync(path, headCtx, ct);

        Assert.Equal(StatusCodes.Status200OK, headCtx.Response.StatusCode);
        Assert.Equal(tagsBody.Length, headCtx.Response.ContentLength);
        Assert.Equal("application/json", headCtx.Response.ContentType);
        Assert.Equal(0, headCtx.Response.Body.Length); // HEAD: no body written
        Assert.Equal(1, Volatile.Read(ref callCount)); // HEAD served from cache
    }

    [Fact]
    public async Task TagsList_UpstreamContentLengthTooLarge_CachesAndServesExactBytes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = "library/nginx/tags/list";
        byte[] tagsBody = "{\"name\":\"nginx\",\"tags\":[\"latest\"]}"u8.ToArray();
        int callCount = 0;

        var upstreamHandler = new TestHttpMessageHandler(request =>
        {
            Interlocked.Increment(ref callCount);
            var content = new ByteArrayContent(tagsBody)
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") },
            };

            // Upstream advertises MORE bytes than it actually sends (truncated/malformed
            // response). The body buffer must reflect the real bytes, never zero-padding.
            content.Headers.ContentLength = tagsBody.Length + 64;

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"truncated-etag\"");
            return response;
        });

        CachingRegistryService service = new CachingRegistryServiceBuilder(_metrics)
            .WithUpstreamHandler(upstreamHandler)
            .WithStore(CreateStore())
            .WithCacheOptions(new CacheOptions { Enabled = true, TagsList = new ListCacheOptions { Enabled = true, Ttl = TimeSpan.FromMinutes(1) } })
            .Build();

        // First: miss → buffered and cached using the ACTUAL byte count, not the header.
        HttpContext ctx1 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, ctx1, ct);
        Assert.Equal(StatusCodes.Status200OK, ctx1.Response.StatusCode);
        ctx1.Response.Body.Position = 0;
        Assert.Equal(tagsBody, ((MemoryStream)ctx1.Response.Body).ToArray());

        // Second: cache hit → exact bytes again, no extra upstream call, no trailing zeros.
        HttpContext ctx2 = CachingRegistryServiceBuilder.CreateHttpContext("GET", path);
        await service.HandleAsync(path, ctx2, ct);
        Assert.Equal(StatusCodes.Status200OK, ctx2.Response.StatusCode);
        ctx2.Response.Body.Position = 0;
        Assert.Equal(tagsBody, ((MemoryStream)ctx2.Response.Body).ToArray());
        Assert.Equal(1, Volatile.Read(ref callCount));
    }

    // IContentStore decorator that returns false for ExistsAsync on all keys,
    // while delegating TryGetAsync and BeginWriteAsync to the inner store.
    // Used to simulate body eviction where the content still exists on disk
    // but IsBodyCachedAsync must report false.
    private sealed class EvictedBodyStore(IContentStore inner) : IContentStore
    {
        public ValueTask<bool> ExistsAsync(string key, CancellationToken ct)
            => ValueTask.FromResult(false);

        public ValueTask<CachedContent?> TryGetAsync(string key, CancellationToken ct)
            => inner.TryGetAsync(key, ct);

        public ValueTask<ICacheWriteHandle> BeginWriteAsync(string key, Digest expectedDigest, CancellationToken ct)
            => inner.BeginWriteAsync(key, expectedDigest, ct);
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

    private static string ComputeSha256Digest(byte[] content)
    {
        byte[] hash = SHA256.HashData(content);
        return $"sha256:{Convert.ToHexStringLower(hash)}";
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

    // An async-capable HttpMessageHandler used to block upstream responses in tests
    // so concurrent requests can be observed mid-flight.
    private sealed class AsyncTestHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> factory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => factory(request);
    }

    // Minimal IHttpClientFactory that returns a pre-built client for one named client.
    private sealed class SingleClientFactory(string name, HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string clientName) => string.Equals(clientName, name, StringComparison.Ordinal) ? client : new HttpClient();
    }
}
