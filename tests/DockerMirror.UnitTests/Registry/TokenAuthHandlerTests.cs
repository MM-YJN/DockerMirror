using System.Net;
using System.Net.Http.Headers;
using System.Text;

using DockerMirror.Configuration;
using DockerMirror.Diagnostics;
using DockerMirror.Registry;
using DockerMirror.UnitTests.TestInfrastructure;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DockerMirror.UnitTests.Registry;

public sealed class TokenAuthHandlerTests : IDisposable
{
    private readonly MirrorMetrics _metrics = new();

    public void Dispose() => _metrics.Dispose();
    private static UpstreamOptions DefaultOptions => new()
    {
        RegistryUrl = "https://registry-1.docker.io",
        TokenRealm = "https://auth.docker.io/token",
        TokenService = "registry.docker.io",
    };

    private TokenAuthHandler CreateHandler(
        HttpMessageHandler tokenHttpHandler,
        HttpMessageHandler innerHandler,
        UpstreamOptions? options = null)
    {
        var tokenHttpClient = new HttpClient(tokenHttpHandler)
        {
            BaseAddress = new Uri("https://auth.docker.io/token"),
        };

        var tokenService = new UpstreamTokenService(
            tokenHttpClient,
            Options.Create(new MirrorOptions { Upstream = options ?? DefaultOptions }),
            NullLogger<UpstreamTokenService>.Instance,
            TimeProvider.System,
            _metrics);

        var handler = new TokenAuthHandler(tokenService, NullLogger<TokenAuthHandler>.Instance)
        {
            InnerHandler = innerHandler,
        };

        return handler;
    }

    [Fact]
    public async Task SendAsync_AttachesBearerToken()
    {
        var tokenHandler = new TestHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"token\":\"test-bearer-token\"}", Encoding.UTF8, "application/json"),
        });

        string? capturedAuthHeader = null;
        var innerHandler = new TestHttpMessageHandler(request =>
        {
            capturedAuthHeader = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        TokenAuthHandler handler = CreateHandler(tokenHandler, innerHandler);
        var invoker = new HttpMessageInvoker(handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://registry-1.docker.io/v2/nginx/manifests/latest");
        await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal("Bearer test-bearer-token", capturedAuthHeader);
    }

    [Fact]
    public async Task SendAsync_401WithWwwAuthenticate_InvalidatesAndRetries()
    {
        var tokenHandler = new TestHttpMessageHandler(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"token\":\"fresh-after-401\"}", Encoding.UTF8, "application/json"),
            });

        bool firstRequest = true;
        var innerHandler = new TestHttpMessageHandler(request =>
        {
            if (firstRequest)
            {
                firstRequest = false;
                var rsp = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                rsp.Headers.WwwAuthenticate.Add(
                    new AuthenticationHeaderValue("Bearer", "realm=\"https://auth.docker.io/token\",service=\"registry.docker.io\",scope=\"repository:nginx:pull\""));
                return rsp;
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        TokenAuthHandler handler = CreateHandler(tokenHandler, innerHandler);
        var invoker = new HttpMessageInvoker(handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://registry-1.docker.io/v2/nginx/manifests/latest");
        HttpResponseMessage response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SendAsync_401WithNoScope_FallsBackToComputedScope()
    {
        string? requestedScope = null;
        var tokenHandler = new TestHttpMessageHandler(request =>
        {
            requestedScope = request.RequestUri?.Query;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"token\":\"fallback-token\"}", Encoding.UTF8, "application/json"),
            };
        });

        var innerHandler = new TestHttpMessageHandler(request =>
        {
            var rsp = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            rsp.Headers.WwwAuthenticate.Add(
                new AuthenticationHeaderValue("Bearer", "realm=\"https://auth.docker.io/token\",service=\"registry.docker.io\""));
            return rsp;
        });

        TokenAuthHandler handler = CreateHandler(tokenHandler, innerHandler);
        var invoker = new HttpMessageInvoker(handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://registry-1.docker.io/v2/nginx/manifests/latest");
        await invoker.SendAsync(request, CancellationToken.None);

        Assert.Contains("scope=repository%3Anginx%3Apull", requestedScope, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_401WithNoWwwAuthenticate_FallsBackToComputedScope()
    {
        string? requestedScope = null;
        var tokenHandler = new TestHttpMessageHandler(request =>
        {
            requestedScope = request.RequestUri?.Query;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"token\":\"fallback-token\"}", Encoding.UTF8, "application/json"),
            };
        });

        var innerHandler = new TestHttpMessageHandler(request =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized));

        TokenAuthHandler handler = CreateHandler(tokenHandler, innerHandler);
        var invoker = new HttpMessageInvoker(handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://registry-1.docker.io/v2/nginx/manifests/latest");
        await invoker.SendAsync(request, CancellationToken.None);

        Assert.Contains("scope=repository%3Anginx%3Apull", requestedScope, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_401WithNonBearerScheme_IgnoresAndFallsBack()
    {
        string? requestedScope = null;
        var tokenHandler = new TestHttpMessageHandler(request =>
        {
            requestedScope = request.RequestUri?.Query;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"token\":\"ignored-basic\"}", Encoding.UTF8, "application/json"),
            };
        });

        var innerHandler = new TestHttpMessageHandler(request =>
        {
            var rsp = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            rsp.Headers.WwwAuthenticate.Add(
                new AuthenticationHeaderValue("Basic", "realm=\"https://auth.docker.io/token\""));
            rsp.Headers.WwwAuthenticate.Add(
                new AuthenticationHeaderValue("Bearer", "realm=\"https://auth.docker.io/token\",scope=\"repository:nginx:pull\""));
            return rsp;
        });

        TokenAuthHandler handler = CreateHandler(tokenHandler, innerHandler);
        var invoker = new HttpMessageInvoker(handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://registry-1.docker.io/v2/nginx/manifests/latest");
        await invoker.SendAsync(request, CancellationToken.None);

        Assert.Contains("scope=repository%3Anginx%3Apull", requestedScope, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_RetryReturns401_PropagatesStatus()
    {
        int tokenCallCount = 0;
        var tokenHandler = new TestHttpMessageHandler(request =>
        {
            tokenCallCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"token\":\"token-" + tokenCallCount + "\"}", Encoding.UTF8, "application/json"),
            };
        });

        var innerHandler = new TestHttpMessageHandler(request =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized));

        TokenAuthHandler handler = CreateHandler(tokenHandler, innerHandler);
        var invoker = new HttpMessageInvoker(handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://registry-1.docker.io/v2/nginx/manifests/latest");
        HttpResponseMessage response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SendAsync_Non401Response_DoesNotRetry()
    {
        int tokenCallCount = 0;
        var tokenHandler = new TestHttpMessageHandler(request =>
        {
            tokenCallCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"token\":\"ok-token\"}", Encoding.UTF8, "application/json"),
            };
        });

        int innerCallCount = 0;
        var innerHandler = new TestHttpMessageHandler(request =>
        {
            innerCallCount++;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        TokenAuthHandler handler = CreateHandler(tokenHandler, innerHandler);
        var invoker = new HttpMessageInvoker(handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://registry-1.docker.io/v2/nginx/manifests/latest");
        HttpResponseMessage response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, tokenCallCount);
        Assert.Equal(1, innerCallCount);
    }

    [Fact]
    public async Task SendAsync_QuotedScope_ExtractsCorrectly()
    {
        string? requestedScope = null;
        var tokenHandler = new TestHttpMessageHandler(request =>
        {
            requestedScope = request.RequestUri?.Query;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"token\":\"quoted-scope-token\"}", Encoding.UTF8, "application/json"),
            };
        });

        bool firstRequest = true;
        var innerHandler = new TestHttpMessageHandler(request =>
        {
            if (firstRequest)
            {
                firstRequest = false;
                var rsp = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                rsp.Headers.WwwAuthenticate.Add(
                    new AuthenticationHeaderValue("Bearer", "realm=\"https://auth.docker.io/token\",scope=\"repository:nginx:pull\""));
                return rsp;
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        TokenAuthHandler handler = CreateHandler(tokenHandler, innerHandler);
        var invoker = new HttpMessageInvoker(handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://registry-1.docker.io/v2/nginx/manifests/latest");
        await invoker.SendAsync(request, CancellationToken.None);

        Assert.Contains("scope=repository%3Anginx%3Apull", requestedScope, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_CatalogPath_ComputesCatalogScope()
    {
        string? requestedScope = null;
        var tokenHandler = new TestHttpMessageHandler(request =>
        {
            requestedScope = request.RequestUri?.Query;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"token\":\"catalog-token\"}", Encoding.UTF8, "application/json"),
            };
        });

        var innerHandler = new TestHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        TokenAuthHandler handler = CreateHandler(tokenHandler, innerHandler);
        var invoker = new HttpMessageInvoker(handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://registry-1.docker.io/v2/_catalog");
        await invoker.SendAsync(request, CancellationToken.None);

        Assert.Contains("scope=registry%3Acatalog%3A%2A", requestedScope, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_ReferrersPath_ComputesRepoPullScope()
    {
        string? requestedScope = null;
        var tokenHandler = new TestHttpMessageHandler(request =>
        {
            requestedScope = request.RequestUri?.Query;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"token\":\"referrers-token\"}", Encoding.UTF8, "application/json"),
            };
        });

        var innerHandler = new TestHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        TokenAuthHandler handler = CreateHandler(tokenHandler, innerHandler);
        var invoker = new HttpMessageInvoker(handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://registry-1.docker.io/v2/nginx/referrers/sha256:abc");
        await invoker.SendAsync(request, CancellationToken.None);

        Assert.Contains("scope=repository%3Anginx%3Apull", requestedScope, StringComparison.Ordinal);
    }
}
