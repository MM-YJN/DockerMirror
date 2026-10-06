using System.Net;
using System.Security.Cryptography;
using System.Text;

using DockerMirror.IntegrationTests.TestInfrastructure;

namespace DockerMirror.IntegrationTests.Registry;

public sealed class CacheIntegrationTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task CacheEnabled_FirstGetFetchesFromUpstream_SecondGetServedFromCache()
    {
        await using var factory = new MirrorTestFactory(testOutputHelper);

        byte[] blobContent = "hello-from-upstream-blob"u8.ToArray();
        string digest = ComputeSha256Digest(blobContent);
        int requestCount = 0;

        factory.Handler.SetupToken(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"token\":\"cache-test-token\",\"expires_in\":300}}",
                    Encoding.UTF8,
                    "application/json"),
            });

        factory.Handler.SetupRegistry(request =>
        {
            Interlocked.Increment(ref requestCount);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(blobContent)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream") },
                },
            };
        });

        using HttpClient client = factory.CreateClient();

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        // First GET: should hit upstream
        HttpResponseMessage response1 = await client.GetAsync($"/v2/nginx/blobs/{digest}", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal("application/octet-stream", response1.Content.Headers.ContentType?.ToString());
        byte[] body1 = await response1.Content.ReadAsByteArrayAsync(cancellationToken);
        Assert.Equal(blobContent, body1);

        Assert.Equal(1, Volatile.Read(ref requestCount));

        // Second GET: should be served from cache (no upstream call)
        HttpResponseMessage response2 = await client.GetAsync($"/v2/nginx/blobs/{digest}", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        byte[] body2 = await response2.Content.ReadAsByteArrayAsync(cancellationToken);
        Assert.Equal(blobContent, body2);
        Assert.Equal(1, Volatile.Read(ref requestCount)); // still 1
    }

    [Fact]
    public async Task TagReference_CachesManifest_WhenDockerContentDigestPresent()
    {
        await using var factory = new MirrorTestFactory(testOutputHelper);

        byte[] content = "tag-manifest-integration-body"u8.ToArray();
        string digest = ComputeSha256Digest(content);
        int requestCount = 0;

        factory.Handler.SetupToken(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"token\":\"tag-cache-token\",\"expires_in\":300}}",
                    Encoding.UTF8,
                    "application/json"),
            });

        factory.Handler.SetupRegistry(request =>
        {
            Interlocked.Increment(ref requestCount);
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

        using HttpClient client = factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        // First GET: upstream, caches body and pointer
        HttpResponseMessage r1 = await client.GetAsync("/v2/nginx/manifests/v1.0", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);

        // Second GET: served from cache
        HttpResponseMessage r2 = await client.GetAsync("/v2/nginx/manifests/v1.0", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.Equal(1, Volatile.Read(ref requestCount));
    }

    [Fact]
    public async Task TagReference_Passthrough_WithoutDockerContentDigest()
    {
        await using var factory = new MirrorTestFactory(testOutputHelper);

        int requestCount = 0;

        factory.Handler.SetupToken(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"token\":\"tag-bypass-token\",\"expires_in\":300}}",
                    Encoding.UTF8,
                    "application/json"),
            });

        factory.Handler.SetupRegistry(request =>
        {
            Interlocked.Increment(ref requestCount);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("tag-body", Encoding.UTF8),
            };
        });

        using HttpClient client = factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        // Tags without Docker-Content-Digest always passthrough
        HttpResponseMessage r1 = await client.GetAsync("/v2/nginx/manifests/latest", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);

        HttpResponseMessage r2 = await client.GetAsync("/v2/nginx/manifests/latest", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);

        Assert.Equal(2, Volatile.Read(ref requestCount));
    }

    [Fact]
    public async Task Non200_NegativeCached()
    {
        await using var factory = new MirrorTestFactory(testOutputHelper);

        byte[] blobContent = "non200-blob"u8.ToArray();
        string digest = ComputeSha256Digest(blobContent);
        int requestCount = 0;

        factory.Handler.SetupToken(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"token\":\"non200-token\",\"expires_in\":300}}",
                    Encoding.UTF8,
                    "application/json"),
            });

        factory.Handler.SetupRegistry(request =>
        {
            Interlocked.Increment(ref requestCount);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using HttpClient client = factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        // First GET: hits upstream, returns 404
        HttpResponseMessage response1 = await client.GetAsync($"/v2/nginx/blobs/{digest}", cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response1.StatusCode);

        // Second GET: served from negative cache (no upstream call)
        HttpResponseMessage response2 = await client.GetAsync($"/v2/nginx/blobs/{digest}", cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response2.StatusCode);
        Assert.Equal("application/json", response2.Content.Headers.ContentType?.ToString());
        Assert.True(response2.Headers.TryGetValues("Docker-Distribution-Api-Version", out _));

        string body = await response2.Content.ReadAsStringAsync(cancellationToken);
        Assert.Contains("BLOB_UNKNOWN", body);

        Assert.Equal(1, Volatile.Read(ref requestCount));
    }

    [Fact]
    public async Task Non200_NegativeCacheDisabled_NotCached()
    {
        await using var factory = new MirrorTestFactory(testOutputHelper, cacheEnabled: true, negativeCacheEnabled: false);

        byte[] blobContent = "non200-disabled"u8.ToArray();
        string digest = ComputeSha256Digest(blobContent);
        int requestCount = 0;

        factory.Handler.SetupToken(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"token\":\"non200-disabled-token\",\"expires_in\":300}}",
                    Encoding.UTF8,
                    "application/json"),
            });

        factory.Handler.SetupRegistry(request =>
        {
            Interlocked.Increment(ref requestCount);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using HttpClient client = factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        HttpResponseMessage response1 = await client.GetAsync($"/v2/nginx/blobs/{digest}", cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response1.StatusCode);

        HttpResponseMessage response2 = await client.GetAsync($"/v2/nginx/blobs/{digest}", cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response2.StatusCode);

        // Negative cache disabled: both requests hit upstream
        Assert.Equal(2, Volatile.Read(ref requestCount));
    }

    [Fact]
    public async Task Upstream500_NotCached()
    {
        await using var factory = new MirrorTestFactory(testOutputHelper);

        byte[] blobContent = "upstream500-blob"u8.ToArray();
        string digest = ComputeSha256Digest(blobContent);
        int requestCount = 0;

        factory.Handler.SetupToken(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"token\":\"upstream500-token\",\"expires_in\":300}}",
                    Encoding.UTF8,
                    "application/json"),
            });

        factory.Handler.SetupRegistry(request =>
        {
            Interlocked.Increment(ref requestCount);
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        using HttpClient client = factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        HttpResponseMessage response1 = await client.GetAsync($"/v2/nginx/blobs/{digest}", cancellationToken);
        Assert.Equal(HttpStatusCode.InternalServerError, response1.StatusCode);

        HttpResponseMessage response2 = await client.GetAsync($"/v2/nginx/blobs/{digest}", cancellationToken);
        Assert.Equal(HttpStatusCode.InternalServerError, response2.StatusCode);
        Assert.False(response2.Headers.TryGetValues("Docker-Distribution-Api-Version", out _));

        // 500 should not be negatively cached
        Assert.Equal(2, Volatile.Read(ref requestCount));
    }

    [Fact]
    public async Task TagManifest404_NegativeCached()
    {
        await using var factory = new MirrorTestFactory(testOutputHelper);

        int requestCount = 0;

        factory.Handler.SetupToken(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"token\":\"tag404-token\",\"expires_in\":300}}",
                    Encoding.UTF8,
                    "application/json"),
            });

        factory.Handler.SetupRegistry(request =>
        {
            Interlocked.Increment(ref requestCount);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using HttpClient client = factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        HttpResponseMessage response1 = await client.GetAsync("/v2/nginx/manifests/nonexistent", cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response1.StatusCode);

        HttpResponseMessage response2 = await client.GetAsync("/v2/nginx/manifests/nonexistent", cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response2.StatusCode);
        Assert.Equal("application/json", response2.Content.Headers.ContentType?.ToString());
        Assert.True(response2.Headers.TryGetValues("Docker-Distribution-Api-Version", out _));

        string body = await response2.Content.ReadAsStringAsync(cancellationToken);
        Assert.Contains("MANIFEST_UNKNOWN", body);

        Assert.Equal(1, Volatile.Read(ref requestCount));
    }

    [Fact]
    public async Task RangeCacheHit_ReturnsPartialContent()
    {
        await using var factory = new MirrorTestFactory(testOutputHelper);

        byte[] blobContent = Encoding.UTF8.GetBytes("hello-from-upstream-blob-range-test");
        string digest = ComputeSha256Digest(blobContent);

        factory.Handler.SetupToken(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"token\":\"range-token\",\"expires_in\":300}}",
                    Encoding.UTF8,
                    "application/json"),
            });

        factory.Handler.SetupRegistry(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(blobContent)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream") },
                },
            });

        using HttpClient client = factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        // First: full GET to populate cache
        HttpResponseMessage full = await client.GetAsync($"/v2/nginx/blobs/{digest}", cancellationToken);
        full.EnsureSuccessStatusCode();

        // Second: range GET on cache hit
        var rangeRequest = new HttpRequestMessage(HttpMethod.Get, $"/v2/nginx/blobs/{digest}");
        rangeRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 4);
        HttpResponseMessage rangeResponse = await client.SendAsync(rangeRequest, cancellationToken);

        Assert.Equal(HttpStatusCode.PartialContent, rangeResponse.StatusCode);
        string? contentRange = rangeResponse.Content.Headers.ContentRange?.ToString();
        Assert.Equal($"bytes 0-4/{blobContent.Length}", contentRange);
        byte[] rangeBody = await rangeResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        Assert.Equal(5, rangeBody.Length);
        Assert.Equal(blobContent[..5], rangeBody);
    }

    [Fact]
    public async Task UnsatisfiableRange_Returns416()
    {
        await using var factory = new MirrorTestFactory(testOutputHelper);

        byte[] blobContent = Encoding.UTF8.GetBytes("range-416-test-content");
        string digest = ComputeSha256Digest(blobContent);

        factory.Handler.SetupToken(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"token\":\"range-416-token\",\"expires_in\":300}}",
                    Encoding.UTF8,
                    "application/json"),
            });

        factory.Handler.SetupRegistry(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(blobContent)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream") },
                },
            });

        using HttpClient client = factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        // First: full GET to populate cache
        HttpResponseMessage full = await client.GetAsync($"/v2/nginx/blobs/{digest}", cancellationToken);
        full.EnsureSuccessStatusCode();

        // Second: unsatisfiable range GET on cache hit
        var rangeRequest = new HttpRequestMessage(HttpMethod.Get, $"/v2/nginx/blobs/{digest}");
        rangeRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(blobContent.Length + 10, null);
        HttpResponseMessage rangeResponse = await client.SendAsync(rangeRequest, cancellationToken);

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, rangeResponse.StatusCode);
        string? cr416 = rangeResponse.Content.Headers.ContentRange?.ToString();
        Assert.Equal($"bytes */{blobContent.Length}", cr416);
    }

    [Fact]
    public async Task CacheHit_SetsDockerContentDigestHeader()
    {
        await using var factory = new MirrorTestFactory(testOutputHelper);

        byte[] blobContent = "digest-header-test"u8.ToArray();
        string digest = ComputeSha256Digest(blobContent);

        factory.Handler.SetupToken(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"token\":\"digest-token\",\"expires_in\":300}}",
                    Encoding.UTF8,
                    "application/json"),
            });

        factory.Handler.SetupRegistry(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(blobContent),
            });

        using HttpClient client = factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        // First: populate cache
        HttpResponseMessage r1 = await client.GetAsync($"/v2/nginx/blobs/{digest}", cancellationToken);
        r1.EnsureSuccessStatusCode();

        // Second: check Docker-Content-Digest
        HttpResponseMessage r2 = await client.GetAsync($"/v2/nginx/blobs/{digest}", cancellationToken);
        r2.EnsureSuccessStatusCode();
        Assert.True(r2.Headers.TryGetValues("Docker-Content-Digest", out IEnumerable<string>? values));
        Assert.Contains(digest, values);
    }

    [Fact]
    public async Task CacheDisabled_EveryRequestGoesUpstream()
    {
        // Regression guard: when Mirror:Cache:Enabled=false the pipeline must be
        // byte-for-byte the same as before the caching layer was added — every
        // request reaches the upstream regardless of whether the digest was seen before.
        await using var disabledFactory = new MirrorTestFactory(testOutputHelper, cacheEnabled: false);

        byte[] blobContent = "disabled-cache-regression-test"u8.ToArray();
        string digest = ComputeSha256Digest(blobContent);
        int requestCount = 0;

        disabledFactory.Handler.SetupToken(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"token\":\"t\",\"expires_in\":300}",
                    Encoding.UTF8,
                    "application/json"),
            });

        disabledFactory.Handler.SetupRegistry(request =>
        {
            Interlocked.Increment(ref requestCount);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(blobContent),
            };
        });

        using HttpClient client = disabledFactory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await client.GetAsync($"/v2/nginx/blobs/{digest}", cancellationToken);
        await client.GetAsync($"/v2/nginx/blobs/{digest}", cancellationToken);

        // Both requests must have hit upstream — cache is not in play.
        Assert.Equal(2, Volatile.Read(ref requestCount));
    }

    private static string ComputeSha256Digest(byte[] content)
    {
        byte[] hash = SHA256.HashData(content);
        return $"sha256:{Convert.ToHexStringLower(hash)}";
    }
}
