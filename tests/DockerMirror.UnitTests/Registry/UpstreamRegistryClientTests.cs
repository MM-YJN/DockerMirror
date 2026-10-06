using System.Net;
using System.Text;

using DockerMirror.Configuration;
using DockerMirror.Diagnostics;
using DockerMirror.Registry;
using DockerMirror.UnitTests.TestInfrastructure;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DockerMirror.UnitTests.Registry;

public sealed class UpstreamRegistryClientTests : IDisposable
{
    private readonly DefaultHttpContext _httpContext;
    private readonly MirrorMetrics _metrics = new();

    public UpstreamRegistryClientTests()
    {
        _httpContext = new DefaultHttpContext();
        _httpContext.Request.Method = HttpMethods.Get;
        _httpContext.Response.Body = new MemoryStream();
    }

    public void Dispose()
    {
        _httpContext.Response.Body.Dispose();
        _metrics.Dispose();
    }

    private static UpstreamOptions DefaultOptions => new()
    {
        RegistryUrl = "https://registry-1.docker.io",
        TokenRealm = "https://auth.docker.io/token",
        TokenService = "registry.docker.io",
    };

    private UpstreamRegistryClient CreateClient(HttpMessageHandler handler, UpstreamOptions? options = null)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://registry-1.docker.io/"),
        };

        return new UpstreamRegistryClient(httpClient, Options.Create(new MirrorOptions { Upstream = options ?? DefaultOptions }), NullLogger<UpstreamRegistryClient>.Instance, _metrics);
    }

    [Fact]
    public async Task ForwardAsync_LibraryRewrite_AppliesNamespace()
    {
        string? upstreamPath = null;
        var handler = new TestHttpMessageHandler(request =>
        {
            upstreamPath = request.RequestUri?.PathAndQuery;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        UpstreamRegistryClient client = CreateClient(handler);

        await client.ForwardAsync("nginx/manifests/latest", _httpContext.Request, _httpContext.Response, CancellationToken.None);

        Assert.Equal("/v2/library/nginx/manifests/latest", upstreamPath);
    }

    [Fact]
    public async Task ForwardAsync_MultiSegmentName_NoRewrite()
    {
        string? upstreamPath = null;
        var handler = new TestHttpMessageHandler(request =>
        {
            upstreamPath = request.RequestUri?.PathAndQuery;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        UpstreamRegistryClient client = CreateClient(handler);

        await client.ForwardAsync("org/app/manifests/latest", _httpContext.Request, _httpContext.Response, CancellationToken.None);

        Assert.Equal("/v2/org/app/manifests/latest", upstreamPath);
    }

    [Fact]
    public async Task ForwardAsync_CustomDefaultNamespace_Applied()
    {
        var options = new UpstreamOptions
        {
            RegistryUrl = "https://registry-1.docker.io",
            TokenRealm = "https://auth.docker.io/token",
            TokenService = "registry.docker.io",
            DefaultNamespace = "custom-ns",
        };
        string? upstreamPath = null;
        var handler = new TestHttpMessageHandler(request =>
        {
            upstreamPath = request.RequestUri?.PathAndQuery;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        UpstreamRegistryClient client = CreateClient(handler, options);

        await client.ForwardAsync("app/manifests/latest", _httpContext.Request, _httpContext.Response, CancellationToken.None);

        Assert.Equal("/v2/custom-ns/app/manifests/latest", upstreamPath);
    }

    [Fact]
    public async Task ForwardAsync_SafeRequestHeaders_Forwarded()
    {
        string? acceptHeader = null;
        var handler = new TestHttpMessageHandler(request =>
        {
            acceptHeader = request.Headers.Accept.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        _httpContext.Request.Headers.Accept = "application/vnd.docker.distribution.manifest.v2+json";
        _httpContext.Request.Headers.UserAgent = "test-agent";

        UpstreamRegistryClient client = CreateClient(handler);

        await client.ForwardAsync("nginx/manifests/latest", _httpContext.Request, _httpContext.Response, CancellationToken.None);

        Assert.Contains("application/vnd.docker.distribution.manifest.v2+json", acceptHeader);
    }

    [Fact]
    public async Task ForwardAsync_UnsafeRequestHeaders_Dropped()
    {
        bool hasAuth = false;
        var handler = new TestHttpMessageHandler(request =>
        {
            hasAuth = request.Headers.Authorization is not null;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        _httpContext.Request.Headers.Authorization = "Bearer some-token";

        UpstreamRegistryClient client = CreateClient(handler);

        await client.ForwardAsync("nginx/manifests/latest", _httpContext.Request, _httpContext.Response, CancellationToken.None);

        Assert.False(hasAuth);
    }

    [Fact]
    public async Task ForwardAsync_ResponseStatusCode_Relayed()
    {
        var handler = new TestHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.NotFound));
        UpstreamRegistryClient client = CreateClient(handler);

        await client.ForwardAsync("nginx/manifests/latest", _httpContext.Request, _httpContext.Response, CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, _httpContext.Response.StatusCode);
    }

    [Fact]
    public async Task ForwardAsync_ResponseHeaders_Relayed()
    {
        var handler = new TestHttpMessageHandler(request =>
        {
            var rsp = new HttpResponseMessage(HttpStatusCode.OK);
            rsp.Headers.Add("Docker-Content-Digest", "sha256:abc123");
            rsp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/vnd.docker.distribution.manifest.v2+json");
            return rsp;
        });

        UpstreamRegistryClient client = CreateClient(handler);

        await client.ForwardAsync("nginx/manifests/latest", _httpContext.Request, _httpContext.Response, CancellationToken.None);

        Assert.True(_httpContext.Response.Headers.ContainsKey("Docker-Content-Digest"));
        Assert.Equal("sha256:abc123", _httpContext.Response.Headers["Docker-Content-Digest"]);
    }

    [Fact]
    public async Task ForwardAsync_HopByHopHeaders_Stripped()
    {
        var handler = new TestHttpMessageHandler(request =>
        {
            var rsp = new HttpResponseMessage(HttpStatusCode.OK);
            rsp.Headers.TransferEncoding.Add(new System.Net.Http.Headers.TransferCodingHeaderValue("chunked"));
            rsp.Headers.Add("Connection", "keep-alive");
            rsp.Headers.Add("Keep-Alive", "timeout=5");
            return rsp;
        });

        UpstreamRegistryClient client = CreateClient(handler);

        await client.ForwardAsync("nginx/manifests/latest", _httpContext.Request, _httpContext.Response, CancellationToken.None);

        Assert.False(_httpContext.Response.Headers.ContainsKey("Transfer-Encoding"));
        Assert.False(_httpContext.Response.Headers.ContainsKey("Connection"));
        Assert.False(_httpContext.Response.Headers.ContainsKey("Keep-Alive"));
    }

    [Fact]
    public async Task ForwardAsync_GetResponse_BodyStreamed()
    {
        var handler = new TestHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("hello world"u8.ToArray()),
        });

        UpstreamRegistryClient client = CreateClient(handler);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await client.ForwardAsync("nginx/manifests/latest", _httpContext.Request, _httpContext.Response, cancellationToken);

        _httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(_httpContext.Response.Body, Encoding.UTF8, leaveOpen: true);
        string body = await reader.ReadToEndAsync(cancellationToken);

        Assert.Equal("hello world", body);
    }

    [Fact]
    public async Task ForwardAsync_HeadResponse_NoBody()
    {
        var handler = new TestHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("should not be forwarded"u8.ToArray()),
        });

        _httpContext.Request.Method = HttpMethods.Head;

        UpstreamRegistryClient client = CreateClient(handler);

        await client.ForwardAsync("nginx/manifests/latest", _httpContext.Request, _httpContext.Response, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, _httpContext.Response.StatusCode);
        Assert.Equal(0, _httpContext.Response.Body.Length);
    }

    [Fact]
    public async Task ForwardAsync_307Redirect_NoBody_LocationRelayed()
    {
        string cdnUrl = "https://cdn.example.com/blobs/sha256:abc";
        var handler = new TestHttpMessageHandler(request =>
        {
            var rsp = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            rsp.Headers.Location = new Uri(cdnUrl);
            rsp.Content = new ByteArrayContent("should not be forwarded"u8.ToArray());
            return rsp;
        });

        UpstreamRegistryClient client = CreateClient(handler);

        await client.ForwardAsync("nginx/blobs/sha256:abc", _httpContext.Request, _httpContext.Response, CancellationToken.None);

        Assert.Equal(StatusCodes.Status307TemporaryRedirect, _httpContext.Response.StatusCode);
        Assert.Equal(0, _httpContext.Response.Body.Length);
        Assert.Equal(cdnUrl, _httpContext.Response.Headers.Location);
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]       // 301
    [InlineData(HttpStatusCode.Redirect)]               // 302
    [InlineData(HttpStatusCode.RedirectMethod)]          // 303
    [InlineData(HttpStatusCode.TemporaryRedirect)]       // 307
    [InlineData(HttpStatusCode.PermanentRedirect)]       // 308
    public async Task ForwardAsync_RedirectStatuses_NoBody(HttpStatusCode statusCode)
    {
        var handler = new TestHttpMessageHandler(new HttpResponseMessage(statusCode)
        {
            Content = new ByteArrayContent("should not be forwarded"u8.ToArray()),
        });

        UpstreamRegistryClient client = CreateClient(handler);

        await client.ForwardAsync("nginx/manifests/latest", _httpContext.Request, _httpContext.Response, CancellationToken.None);

        Assert.Equal((int)statusCode, _httpContext.Response.StatusCode);
        Assert.Equal(0, _httpContext.Response.Body.Length);
    }

    [Fact]
    public async Task ForwardAsync_QueryString_Appended()
    {
        string? upstreamPath = null;
        var handler = new TestHttpMessageHandler(request =>
        {
            upstreamPath = request.RequestUri?.PathAndQuery;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        _httpContext.Request.QueryString = new QueryString("?ns=docker.io");

        UpstreamRegistryClient client = CreateClient(handler);

        await client.ForwardAsync("nginx/tags/list", _httpContext.Request, _httpContext.Response, CancellationToken.None);

        Assert.Equal("/v2/library/nginx/tags/list?ns=docker.io", upstreamPath);
    }

    [Fact]
    public async Task ForwardAsync_BadPath_Returns400WithError()
    {
        var handler = new TestHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        UpstreamRegistryClient client = CreateClient(handler);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await client.ForwardAsync("not-a-valid-registry-path", _httpContext.Request, _httpContext.Response, cancellationToken);

        Assert.Equal(StatusCodes.Status400BadRequest, _httpContext.Response.StatusCode);
        Assert.Equal("application/json", _httpContext.Response.ContentType);
        Assert.True(_httpContext.Response.Headers.ContainsKey("Docker-Distribution-Api-Version"));

        _httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(_httpContext.Response.Body, Encoding.UTF8, leaveOpen: true);
        string body = await reader.ReadToEndAsync(cancellationToken);

        Assert.Contains("UNSUPPORTED", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForwardAsync_WithoutReference_OmitsReferenceSegment()
    {
        string? upstreamPath = null;
        var handler = new TestHttpMessageHandler(request =>
        {
            upstreamPath = request.RequestUri?.PathAndQuery;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        UpstreamRegistryClient client = CreateClient(handler);

        await client.ForwardAsync("nginx/blobs/uploads", _httpContext.Request, _httpContext.Response, CancellationToken.None);

        Assert.Equal("/v2/library/nginx/blobs/uploads", upstreamPath);
    }

    [Fact]
    public async Task ForwardRawAsync_PathPassedVerbatim()
    {
        string? upstreamPath = null;
        var handler = new TestHttpMessageHandler(request =>
        {
            upstreamPath = request.RequestUri?.PathAndQuery;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        UpstreamRegistryClient client = CreateClient(handler);

        await client.ForwardRawAsync("v2/_catalog", _httpContext.Request, _httpContext.Response, CancellationToken.None);

        Assert.Equal("/v2/_catalog", upstreamPath);
    }

    [Fact]
    public async Task ForwardRawAsync_QueryString_Appended()
    {
        string? upstreamPath = null;
        var handler = new TestHttpMessageHandler(request =>
        {
            upstreamPath = request.RequestUri?.PathAndQuery;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        _httpContext.Request.QueryString = new QueryString("?n=10");

        UpstreamRegistryClient client = CreateClient(handler);

        await client.ForwardRawAsync("v2/_catalog", _httpContext.Request, _httpContext.Response, CancellationToken.None);

        Assert.Equal("/v2/_catalog?n=10", upstreamPath);
    }

    [Fact]
    public async Task ForwardRawAsync_HeadResponse_NoBody()
    {
        var handler = new TestHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("should not be forwarded"u8.ToArray()),
        });

        _httpContext.Request.Method = HttpMethods.Head;

        UpstreamRegistryClient client = CreateClient(handler);

        await client.ForwardRawAsync("v2/_catalog", _httpContext.Request, _httpContext.Response, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, _httpContext.Response.StatusCode);
        Assert.Equal(0, _httpContext.Response.Body.Length);
    }
}
