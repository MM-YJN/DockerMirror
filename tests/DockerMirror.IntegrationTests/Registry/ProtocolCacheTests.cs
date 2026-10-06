using System.Net;
using System.Security.Cryptography;
using System.Text;

using DockerMirror.Configuration;
using DockerMirror.IntegrationTests.TestInfrastructure;

using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace DockerMirror.IntegrationTests.Registry;

public sealed class ProtocolCacheTests(ITestOutputHelper testOutputHelper)
{
    private static string ComputeSha256Digest(ReadOnlySpan<byte> content)
        => $"sha256:{Convert.ToHexStringLower(SHA256.HashData(content))}";

    [Fact]
    public async Task DigestBlob_CacheHit_IncludesETagAndCacheControlImmutable()
    {
        await using var factory = new MirrorTestFactory(testOutputHelper);

        byte[] blobContent = "protocol-cache-blob"u8.ToArray();
        string digest = ComputeSha256Digest(blobContent);
        int requestCount = 0;

        factory.Handler.SetupToken(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"token\":\"immutable-token\",\"expires_in\":300}}",
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
        CancellationToken ct = TestContext.Current.CancellationToken;

        // First: miss
        await client.GetAsync($"/v2/nginx/blobs/{digest}", ct);

        // Second: cache hit — verify headers
        HttpResponseMessage response = await client.GetAsync($"/v2/nginx/blobs/{digest}", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"\"{digest}\"", response.Headers.ETag?.ToString());
        Assert.NotNull(response.Headers.CacheControl);
        Assert.Contains("immutable", response.Headers.CacheControl!.ToString());
        Assert.Contains("max-age", response.Headers.CacheControl!.ToString());
        Assert.NotNull(response.Headers.Age);
        Assert.Equal(1, Volatile.Read(ref requestCount));
    }

    [Fact]
    public async Task DigestBlob_IfNoneMatch_Returns304()
    {
        await using var factory = new MirrorTestFactory(testOutputHelper);

        byte[] blobContent = "304-blob"u8.ToArray();
        string digest = ComputeSha256Digest(blobContent);
        int requestCount = 0;

        factory.Handler.SetupToken(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"token\":\"304-token\",\"expires_in\":300}}",
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
        CancellationToken ct = TestContext.Current.CancellationToken;

        // First: populate cache
        await client.GetAsync($"/v2/nginx/blobs/{digest}", ct);

        // Second: If-None-Match → 304
        var request = new HttpRequestMessage(HttpMethod.Get, $"/v2/nginx/blobs/{digest}");
        request.Headers.TryAddWithoutValidation("If-None-Match", $"\"{digest}\"");
        HttpResponseMessage response = await client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(ct));
        Assert.Equal($"\"{digest}\"", response.Headers.ETag?.ToString());
        Assert.Contains("immutable", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task DigestBlob_IfNoneMatch_Mismatch_Returns200()
    {
        await using var factory = new MirrorTestFactory(testOutputHelper);

        byte[] blobContent = "mismatch-blob"u8.ToArray();
        string digest = ComputeSha256Digest(blobContent);
        int requestCount = 0;

        factory.Handler.SetupToken(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"token\":\"mismatch-token\",\"expires_in\":300}}",
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
        CancellationToken ct = TestContext.Current.CancellationToken;

        var request = new HttpRequestMessage(HttpMethod.Get, $"/v2/nginx/blobs/{digest}");
        request.Headers.TryAddWithoutValidation("If-None-Match", "\"sha256:different\"");
        HttpResponseMessage response = await client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        byte[] body = await response.Content.ReadAsByteArrayAsync(ct);
        Assert.Equal(blobContent, body);
    }

    [Fact]
    public async Task TagsList_CachesAndServesFromCache()
    {
        await using var factory = new MirrorTestFactory(testOutputHelper);

        byte[] tagsBody = "{\"name\":\"nginx\",\"tags\":[\"latest\",\"1.0\"]}"u8.ToArray();
        int requestCount = 0;

        factory.Handler.SetupToken(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"token\":\"tags-cache-token\",\"expires_in\":300}}",
                    Encoding.UTF8,
                    "application/json"),
            });

        factory.Handler.SetupRegistry(request =>
        {
            Interlocked.Increment(ref requestCount);
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

        using HttpClient client = factory.CreateClient();
        CancellationToken ct = TestContext.Current.CancellationToken;

        // First: miss → fetch and cache
        HttpResponseMessage r1 = await client.GetAsync("/v2/nginx/tags/list", ct);
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        byte[] body1 = await r1.Content.ReadAsByteArrayAsync(ct);
        Assert.Equal(tagsBody, body1);

        // Second: cache hit
        HttpResponseMessage r2 = await client.GetAsync("/v2/nginx/tags/list", ct);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        byte[] body2 = await r2.Content.ReadAsByteArrayAsync(ct);
        Assert.Equal(tagsBody, body2);

        Assert.Equal(1, Volatile.Read(ref requestCount));
    }

    [Fact]
    public async Task TagsList_CacheHit_IncludesCacheControl()
    {
        await using var factory = new MirrorTestFactory(testOutputHelper);

        byte[] tagsBody = "{\"name\":\"nginx\",\"tags\":[\"latest\"]}"u8.ToArray();
        int requestCount = 0;

        factory.Handler.SetupToken(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"token\":\"tags-cc-token\",\"expires_in\":300}}",
                    Encoding.UTF8,
                    "application/json"),
            });

        factory.Handler.SetupRegistry(request =>
        {
            Interlocked.Increment(ref requestCount);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(tagsBody)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") },
                },
            };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"list-etag\"");
            return response;
        });

        using HttpClient client = factory.CreateClient();
        CancellationToken ct = TestContext.Current.CancellationToken;

        // First: miss
        await client.GetAsync("/v2/nginx/tags/list", ct);

        // Second: cache hit
        HttpResponseMessage r2 = await client.GetAsync("/v2/nginx/tags/list", ct);

        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.Equal("\"list-etag\"", r2.Headers.ETag?.ToString());
        Assert.NotNull(r2.Headers.CacheControl);
        Assert.Contains("max-age", r2.Headers.CacheControl!.ToString());
        Assert.DoesNotContain("immutable", r2.Headers.CacheControl!.ToString());
        Assert.NotNull(r2.Headers.Age);
        Assert.Equal(tagsBody.Length, r2.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task TagsList_IfNoneMatch_Returns304()
    {
        await using var factory = new MirrorTestFactory(testOutputHelper);

        byte[] tagsBody = "{\"name\":\"nginx\",\"tags\":[\"stable\"]}"u8.ToArray();
        int requestCount = 0;

        factory.Handler.SetupToken(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"token\":\"tags-304-token\",\"expires_in\":300}}",
                    Encoding.UTF8,
                    "application/json"),
            });

        factory.Handler.SetupRegistry(request =>
        {
            Interlocked.Increment(ref requestCount);
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

        using HttpClient client = factory.CreateClient();
        CancellationToken ct = TestContext.Current.CancellationToken;

        // First: miss → cache
        await client.GetAsync("/v2/nginx/tags/list", ct);

        // Second: If-None-Match → 304
        var request = new HttpRequestMessage(HttpMethod.Get, "/v2/nginx/tags/list");
        request.Headers.TryAddWithoutValidation("If-None-Match", "\"my-etag\"");
        HttpResponseMessage response = await client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(ct));
        Assert.Equal("\"my-etag\"", response.Headers.ETag?.ToString());
        Assert.NotNull(response.Headers.CacheControl);
        Assert.Equal(1, Volatile.Read(ref requestCount));
    }

    [Fact]
    public async Task TagsList_WithLinkHeader_PreservesLinkOnCacheHit()
    {
        await using var factory = new MirrorTestFactory(testOutputHelper);

        byte[] tagsBody = "{\"name\":\"nginx\",\"tags\":[\"v1\",\"v2\"]}"u8.ToArray();
        string linkHeader = "</v2/nginx/tags/list?n=2&last=v2>; rel=\"next\"";

        factory.Handler.SetupToken(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"token\":\"link-token\",\"expires_in\":300}}",
                    Encoding.UTF8,
                    "application/json"),
            });

        factory.Handler.SetupRegistry(request =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(tagsBody)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") },
                },
            };
            response.Headers.TryAddWithoutValidation("Link", linkHeader);
            return response;
        });

        using HttpClient client = factory.CreateClient();
        CancellationToken ct = TestContext.Current.CancellationToken;

        // First: miss
        HttpResponseMessage r1 = await client.GetAsync("/v2/nginx/tags/list", ct);
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.True(r1.Headers.TryGetValues("Link", out IEnumerable<string>? links1));
        Assert.Equal(linkHeader, string.Join(", ", links1));

        // Second: cache hit
        HttpResponseMessage r2 = await client.GetAsync("/v2/nginx/tags/list", ct);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.True(r2.Headers.TryGetValues("Link", out IEnumerable<string>? links2));
        Assert.Equal(linkHeader, string.Join(", ", links2));
    }

    [Fact]
    public async Task TagsList_Non200_Passthrough_NotCached()
    {
        await using var factory = new MirrorTestFactory(testOutputHelper);

        int callCount = 0;

        factory.Handler.SetupToken(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"token\":\"unauth-token\",\"expires_in\":300}}",
                    Encoding.UTF8,
                    "application/json"),
            });

        factory.Handler.SetupRegistry(request =>
        {
            Interlocked.Increment(ref callCount);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using HttpClient client = factory.CreateClient();
        CancellationToken ct = TestContext.Current.CancellationToken;

        HttpResponseMessage r1 = await client.GetAsync("/v2/nginx/tags/list", ct);
        Assert.Equal(HttpStatusCode.NotFound, r1.StatusCode);

        HttpResponseMessage r2 = await client.GetAsync("/v2/nginx/tags/list", ct);
        Assert.Equal(HttpStatusCode.NotFound, r2.StatusCode);

        Assert.Equal(2, Volatile.Read(ref callCount));
    }

    [Fact]
    public async Task Catalog_Non200_Passthrough_NotCached()
    {
        await using var factory = new MirrorTestFactory(testOutputHelper, configureBuilder: builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.Configure<MirrorOptions>(o =>
                {
                    o.Cache.Catalog.Enabled = true;
                    o.Cache.Catalog.Ttl = TimeSpan.FromMinutes(1);
                });
            });
        });

        int callCount = 0;

        factory.Handler.SetupToken(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"token\":\"cat-token\",\"expires_in\":300}}",
                    Encoding.UTF8,
                    "application/json"),
            });

        factory.Handler.SetupRegistry(request =>
        {
            Interlocked.Increment(ref callCount);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using HttpClient client = factory.CreateClient();
        CancellationToken ct = TestContext.Current.CancellationToken;

        HttpResponseMessage r1 = await client.GetAsync("/v2/_catalog", ct);
        Assert.Equal(HttpStatusCode.NotFound, r1.StatusCode);

        HttpResponseMessage r2 = await client.GetAsync("/v2/_catalog", ct);
        Assert.Equal(HttpStatusCode.NotFound, r2.StatusCode);

        Assert.Equal(2, Volatile.Read(ref callCount));
    }

    [Fact]
    public async Task TagManifest_CachedResponse_IncludesCacheControlWithoutImmutable()
    {
        await using var factory = new MirrorTestFactory(testOutputHelper);

        byte[] content = "tag-cache-control-body"u8.ToArray();
        string digest = ComputeSha256Digest(content);
        int requestCount = 0;

        factory.Handler.SetupToken(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"token\":\"tag-cc-token\",\"expires_in\":300}}",
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
        CancellationToken ct = TestContext.Current.CancellationToken;

        // First: miss → cache
        await client.GetAsync("/v2/nginx/manifests/latest", ct);

        // Second: cache hit
        HttpResponseMessage r2 = await client.GetAsync("/v2/nginx/manifests/latest", ct);

        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.Equal($"\"{digest}\"", r2.Headers.ETag?.ToString());
        Assert.NotNull(r2.Headers.CacheControl);
        Assert.Contains("max-age", r2.Headers.CacheControl!.ToString());
        Assert.DoesNotContain("immutable", r2.Headers.CacheControl!.ToString());
        Assert.NotNull(r2.Headers.Age);
        Assert.Equal(1, Volatile.Read(ref requestCount));
    }

    [Fact]
    public async Task TagManifest_IfNoneMatch_Returns304()
    {
        await using var factory = new MirrorTestFactory(testOutputHelper);

        byte[] content = "tag-304-body"u8.ToArray();
        string digest = ComputeSha256Digest(content);
        int requestCount = 0;

        factory.Handler.SetupToken(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"token\":\"tag-304-token\",\"expires_in\":300}}",
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
        CancellationToken ct = TestContext.Current.CancellationToken;

        // First: miss → cache
        await client.GetAsync("/v2/nginx/manifests/v1.0", ct);

        // Second: If-None-Match → 304
        var request = new HttpRequestMessage(HttpMethod.Get, "/v2/nginx/manifests/v1.0");
        request.Headers.TryAddWithoutValidation("If-None-Match", $"\"{digest}\"");
        HttpResponseMessage response = await client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(ct));
        Assert.Equal($"\"{digest}\"", response.Headers.ETag?.ToString());
        Assert.DoesNotContain("immutable", response.Headers.CacheControl!.ToString());
        Assert.Equal(1, Volatile.Read(ref requestCount));
    }

    [Fact]
    public async Task Catalog_Enabled_Caches200Response()
    {
        await using var factory = new MirrorTestFactory(testOutputHelper, configureBuilder: builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.Configure<MirrorOptions>(o =>
                {
                    o.Cache.Catalog.Enabled = true;
                    o.Cache.Catalog.Ttl = TimeSpan.FromMinutes(1);
                });
            });
        });

        byte[] catalogBody = "{\"repositories\":[\"nginx\",\"redis\"]}"u8.ToArray();
        int requestCount = 0;

        factory.Handler.SetupToken(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"token\":\"cat-cache-token\",\"expires_in\":300}}",
                    Encoding.UTF8,
                    "application/json"),
            });

        factory.Handler.SetupRegistry(request =>
        {
            Interlocked.Increment(ref requestCount);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(catalogBody)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") },
                },
            };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"cat-etag\"");
            return response;
        });

        using HttpClient client = factory.CreateClient();
        CancellationToken ct = TestContext.Current.CancellationToken;

        // First: miss → cache
        HttpResponseMessage r1 = await client.GetAsync("/v2/_catalog", ct);
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);

        // Second: cache hit
        HttpResponseMessage r2 = await client.GetAsync("/v2/_catalog", ct);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.Equal(1, Volatile.Read(ref requestCount));
    }
}
