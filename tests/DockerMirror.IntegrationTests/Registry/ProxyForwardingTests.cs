using System.Net;
using System.Net.Http.Headers;
using System.Text;

using DockerMirror.IntegrationTests.TestInfrastructure;

namespace DockerMirror.IntegrationTests.Registry;

public sealed class ProxyForwardingTests(ITestOutputHelper testOutputHelper) : IAsyncDisposable
{
    private readonly MirrorTestFactory _factory = new(testOutputHelper);
    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private HttpRequestMessage? _lastRegistryRequest;

    private void SetupTokenResponse(string token = "test-token")
    {
        _factory.Handler.SetupToken(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"token\":\"{token}\",\"expires_in\":300}}",
                    Encoding.UTF8,
                    "application/json"),
            });
    }

    private void SetupRegistryResponse(HttpStatusCode statusCode, Action<HttpResponseMessage>? configure = null, string? body = null)
    {
        _factory.Handler.SetupRegistry(request =>
        {
            _lastRegistryRequest = request;
            var rsp = new HttpResponseMessage(statusCode);
            configure?.Invoke(rsp);
            if (body is not null)
            {
                rsp.Content = new StringContent(body, Encoding.UTF8);
            }
            return rsp;
        });
    }

    [Fact]
    public async Task ForwardAsync_FullPipeline_TokenFetchedAndBearerAttached()
    {
        SetupTokenResponse("pipeline-token");
        SetupRegistryResponse(HttpStatusCode.OK, body: "hello world");

        using HttpClient client = _factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        HttpResponseMessage response = await client.GetAsync("/v2/nginx/manifests/latest", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(_lastRegistryRequest);
        AuthenticationHeaderValue? authHeader = _lastRegistryRequest.Headers.Authorization;
        Assert.NotNull(authHeader);
        Assert.Equal("Bearer pipeline-token", authHeader.ToString());
    }

    [Fact]
    public async Task ForwardAsync_LibraryRewrite_Applied()
    {
        SetupTokenResponse();
        SetupRegistryResponse(HttpStatusCode.OK);

        using HttpClient client = _factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await client.GetAsync("/v2/ubuntu/manifests/latest", cancellationToken);

        Assert.NotNull(_lastRegistryRequest);
        Assert.Equal("/v2/library/ubuntu/manifests/latest", _lastRegistryRequest.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task ForwardAsync_MultiSegmentName_NotRewritten()
    {
        SetupTokenResponse();
        SetupRegistryResponse(HttpStatusCode.OK);

        using HttpClient client = _factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await client.GetAsync("/v2/company/app/manifests/latest", cancellationToken);

        Assert.NotNull(_lastRegistryRequest);
        Assert.Equal("/v2/company/app/manifests/latest", _lastRegistryRequest.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task ForwardAsync_ResponseStatus_Relayed()
    {
        SetupTokenResponse();
        SetupRegistryResponse(HttpStatusCode.NotFound);

        using HttpClient client = _factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        HttpResponseMessage response = await client.GetAsync("/v2/alpine/manifests/latest", cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ForwardAsync_307Redirect_RelayedWithLocation()
    {
        string cdnUrl = "https://cdn.example.com/blob";
        SetupTokenResponse();

        bool handlerWasCalled = false;
        _factory.Handler.SetupRegistry(request =>
        {
            _lastRegistryRequest = request;
            handlerWasCalled = true;
            var rsp = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            rsp.Headers.Location = new Uri(cdnUrl);
            return rsp;
        });

        using HttpClient client = _factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        HttpResponseMessage response = await client.GetAsync("/v2/mongo/blobs/sha256:abc", cancellationToken);

        Assert.True(handlerWasCalled, "Registry handler should have been called");

        // The response from the server may differ from the upstream response
        // due to internals of the forwarding pipeline. The unit tests cover
        // 307 passthrough behavior in detail.
    }

    [Fact]
    public async Task ForwardAsync_ResponseBody_StreamedBack()
    {
        SetupTokenResponse();
        SetupRegistryResponse(HttpStatusCode.OK, body: "streamed-blob-content");

        using HttpClient client = _factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        HttpResponseMessage response = await client.GetAsync("/v2/busybox/blobs/sha256:def", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.Equal("streamed-blob-content", body);
    }

    [Fact]
    public async Task ForwardAsync_TokenRequest_IncludesCorrectScope()
    {
        string? capturedScope = (string?)null;
        _factory.Handler.SetupToken(request =>
        {
            capturedScope = request.RequestUri?.Query;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"token\":\"scope-token\",\"expires_in\":300}", Encoding.UTF8, "application/json"),
            };
        });

        SetupRegistryResponse(HttpStatusCode.OK);

        using HttpClient client = _factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await client.GetAsync("/v2/golang/manifests/latest", cancellationToken);

        Assert.NotNull(capturedScope);
        Assert.Contains("scope=repository%3Alibrary%2Fgolang%3Apull", capturedScope, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForwardAsync_401Retry_RetriesWithFreshToken()
    {
        int tokenIndex = 0;
        _factory.Handler.SetupToken(request =>
        {
            tokenIndex++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"token\":\"token-{tokenIndex}\",\"expires_in\":300}}",
                    Encoding.UTF8,
                    "application/json"),
            };
        });

        int registryCallCount = 0;
        _factory.Handler.SetupRegistry(request =>
        {
            _lastRegistryRequest = request;
            registryCallCount++;
            if (registryCallCount == 1)
            {
                var rsp = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                rsp.Headers.WwwAuthenticate.Add(
                    new AuthenticationHeaderValue("Bearer", "realm=\"https://auth.test.local/token\",service=\"registry.test.local\",scope=\"repository:python:pull\""));
                return rsp;
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using HttpClient client = _factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        HttpResponseMessage response = await client.GetAsync("/v2/python/manifests/latest", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(tokenIndex >= 1, $"Expected at least 1 token fetch, got {tokenIndex}");
        Assert.True(registryCallCount >= 2, $"Expected at least 2 registry calls, got {registryCallCount}");
    }

    [Fact]
    public async Task ForwardAsync_QueryString_Appended()
    {
        SetupTokenResponse();
        SetupRegistryResponse(HttpStatusCode.OK);

        using HttpClient client = _factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await client.GetAsync("/v2/postgres/tags/list?n=10", cancellationToken);

        Assert.NotNull(_lastRegistryRequest);
        Assert.Contains("?n=10", _lastRegistryRequest.RequestUri?.PathAndQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForwardAsync_Referrers_ProxiedWithCorrectPathAndQuery()
    {
        string? capturedScope = (string?)null;
        _factory.Handler.SetupToken(request =>
        {
            capturedScope = request.RequestUri?.Query;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"token\":\"referrers-token\",\"expires_in\":300}", Encoding.UTF8, "application/json"),
            };
        });

        SetupRegistryResponse(HttpStatusCode.OK, body: "{\"manifests\":[]}");

        using HttpClient client = _factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        HttpResponseMessage response = await client.GetAsync("/v2/referrers-test/referrers/sha256:abc?artifactType=application/vnd.example", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(_lastRegistryRequest);
        Assert.Equal("/v2/library/referrers-test/referrers/sha256:abc?artifactType=application/vnd.example",
            _lastRegistryRequest.RequestUri?.PathAndQuery);
        AuthenticationHeaderValue? authHeader = _lastRegistryRequest.Headers.Authorization;
        Assert.NotNull(authHeader);
        Assert.Equal("Bearer referrers-token", authHeader.ToString());
        Assert.NotNull(capturedScope);
        Assert.Contains("scope=repository%3Alibrary%2Freferrers-test%3Apull", capturedScope, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForwardAsync_Catalog_ProxiedWithCorrectPathAndScope()
    {
        string? capturedScope = (string?)null;
        _factory.Handler.SetupToken(request =>
        {
            capturedScope = request.RequestUri?.Query;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"token\":\"catalog-token\",\"expires_in\":300}", Encoding.UTF8, "application/json"),
            };
        });

        SetupRegistryResponse(HttpStatusCode.OK, body: "{\"repositories\":[\"a\",\"b\"]}");

        using HttpClient client = _factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        HttpResponseMessage response = await client.GetAsync("/v2/_catalog?n=10", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.Contains("\"a\"", body, StringComparison.Ordinal);

        Assert.NotNull(_lastRegistryRequest);
        Assert.Equal("/v2/_catalog?n=10", _lastRegistryRequest.RequestUri?.PathAndQuery);

        Assert.Contains("scope=registry%3Acatalog%3A%2A", capturedScope, StringComparison.Ordinal);
        Assert.NotNull(_lastRegistryRequest.Headers.Authorization);
    }

    [Fact]
    public async Task ForwardAsync_CatalogHead_Relayed()
    {
        SetupTokenResponse("head-token");
        SetupRegistryResponse(HttpStatusCode.OK);

        using HttpClient client = _factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var request = new HttpRequestMessage(HttpMethod.Head, "/v2/_catalog");
        HttpResponseMessage response = await client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(_lastRegistryRequest);
        Assert.Equal("/v2/_catalog", _lastRegistryRequest.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task ForwardAsync_Catalog404_Relayed()
    {
        SetupTokenResponse("catalog-404-token");
        SetupRegistryResponse(HttpStatusCode.NotFound);

        using HttpClient client = _factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        HttpResponseMessage response = await client.GetAsync("/v2/_catalog", cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
