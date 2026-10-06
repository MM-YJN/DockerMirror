using System.Net;
using System.Text;

using DockerMirror.Configuration;
using DockerMirror.Diagnostics;
using DockerMirror.Registry;
using DockerMirror.UnitTests.TestInfrastructure;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace DockerMirror.UnitTests.Registry;

public sealed class UpstreamTokenServiceTests : IDisposable
{
    private readonly MirrorMetrics _metrics = new();

    public void Dispose() => _metrics.Dispose();
    private static UpstreamOptions DefaultOptions => new()
    {
        RegistryUrl = "https://registry-1.docker.io",
        TokenRealm = "https://auth.docker.io/token",
        TokenService = "registry.docker.io",
    };

    private UpstreamTokenService CreateService(HttpMessageHandler handler, UpstreamOptions? options = null, TimeProvider? timeProvider = null)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://auth.docker.io/token"),
        };

        return new UpstreamTokenService(
            httpClient,
            Options.Create(new MirrorOptions { Upstream = options ?? DefaultOptions }),
            NullLogger<UpstreamTokenService>.Instance,
            timeProvider ?? TimeProvider.System,
            _metrics);
    }

    [Fact]
    public async Task GetTokenAsync_FetchesToken_ReturnsToken()
    {
        var handler = new TestHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"token\":\"test-token-123\"}", Encoding.UTF8, "application/json"),
        });
        UpstreamTokenService service = CreateService(handler);

        string token = await service.GetTokenAsync("repository:nginx:pull", CancellationToken.None);

        Assert.Equal("test-token-123", token);
    }

    [Fact]
    public async Task GetTokenAsync_SameScope_CachesToken()
    {
        int callCount = 0;
        var handler = new TestHttpMessageHandler(request =>
        {
            callCount++;
            string json = "{\"token\":\"cached-token\",\"expires_in\":300}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });

        UpstreamTokenService service = CreateService(handler);

        string token1 = await service.GetTokenAsync("repository:nginx:pull", CancellationToken.None);
        string token2 = await service.GetTokenAsync("repository:nginx:pull", CancellationToken.None);

        Assert.Equal("cached-token", token1);
        Assert.Equal("cached-token", token2);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task GetTokenAsync_DifferentScopes_FetchesSeparately()
    {
        int callCount = 0;
        var handler = new TestHttpMessageHandler(request =>
        {
            callCount++;
            string json = "{\"token\":\"token-" + callCount + "\",\"expires_in\":300}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });

        UpstreamTokenService service = CreateService(handler);

        string token1 = await service.GetTokenAsync("repository:nginx:pull", CancellationToken.None);
        string token2 = await service.GetTokenAsync("repository:app:pull", CancellationToken.None);

        Assert.Equal("token-1", token1);
        Assert.Equal("token-2", token2);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task GetTokenAsync_AfterInvalidate_RefetchesToken()
    {
        int callCount = 0;
        var handler = new TestHttpMessageHandler(request =>
        {
            callCount++;
            string json = "{\"token\":\"token-" + callCount + "\",\"expires_in\":300}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });

        UpstreamTokenService service = CreateService(handler);

        await service.GetTokenAsync("repository:nginx:pull", CancellationToken.None);
        service.Invalidate("repository:nginx:pull");
        string token2 = await service.GetTokenAsync("repository:nginx:pull", CancellationToken.None);

        Assert.Equal("token-2", token2);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task GetTokenAsync_ExpiredToken_Refetches()
    {
        int callCount = 0;
        var handler = new TestHttpMessageHandler(request =>
        {
            callCount++;
            string json = "{\"token\":\"token-" + callCount + "\",\"expires_in\":60}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });

        var time = new FakeTimeProvider();
        UpstreamTokenService service = CreateService(handler, timeProvider: time);

        string token1 = await service.GetTokenAsync("repository:nginx:pull", CancellationToken.None);
        Assert.Equal("token-1", token1);

        time.Advance(TimeSpan.FromSeconds(61));

        string token2 = await service.GetTokenAsync("repository:nginx:pull", CancellationToken.None);
        Assert.Equal("token-2", token2);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task GetTokenAsync_TokenCacheTtl_CapsExpiry()
    {
        var options = new UpstreamOptions
        {
            RegistryUrl = "https://registry-1.docker.io",
            TokenRealm = "https://auth.docker.io/token",
            TokenService = "registry.docker.io",
            TokenCacheTtl = TimeSpan.FromSeconds(30),
        };
        int callCount = 0;
        var handler = new TestHttpMessageHandler(request =>
        {
            callCount++;
            string json = "{\"token\":\"token-" + callCount + "\",\"expires_in\":300}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });

        var time = new FakeTimeProvider();
        UpstreamTokenService service = CreateService(handler, options: options, timeProvider: time);

        await service.GetTokenAsync("repository:nginx:pull", CancellationToken.None);
        Assert.Equal(1, callCount);

        time.Advance(TimeSpan.FromSeconds(31));

        await service.GetTokenAsync("repository:nginx:pull", CancellationToken.None);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task GetTokenAsync_NoAuth_DoesNotSendAuthorizationHeader()
    {
        string? authHeader = null;
        var handler = new TestHttpMessageHandler(request =>
        {
            authHeader = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"token\":\"anon-token\"}", Encoding.UTF8, "application/json"),
            };
        });

        UpstreamTokenService service = CreateService(handler);

        await service.GetTokenAsync("repository:nginx:pull", CancellationToken.None);

        Assert.Null(authHeader);
    }

    [Fact]
    public async Task GetTokenAsync_WithAuth_SendsBasicAuthorizationHeader()
    {
        var options = new UpstreamOptions
        {
            RegistryUrl = "https://registry-1.docker.io",
            TokenRealm = "https://auth.docker.io/token",
            TokenService = "registry.docker.io",
            Auth = new UpstreamAuthOptions { Username = "user", Password = "pass" },
        };

        string? authHeader = null;
        var handler = new TestHttpMessageHandler(request =>
        {
            authHeader = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"token\":\"auth-token\"}", Encoding.UTF8, "application/json"),
            };
        });

        UpstreamTokenService service = CreateService(handler, options: options);

        await service.GetTokenAsync("repository:nginx:pull", CancellationToken.None);

        Assert.NotNull(authHeader);
        Assert.StartsWith("Basic ", authHeader, StringComparison.Ordinal);
        string expectedValue = Convert.ToBase64String(Encoding.UTF8.GetBytes("user:pass"));
        Assert.Equal($"Basic {expectedValue}", authHeader);
    }

    [Fact]
    public async Task GetTokenAsync_TokenField_PreferredOverAccessToken()
    {
        string json = "{\"token\":\"primary-token\",\"access_token\":\"fallback-token\",\"expires_in\":300}";
        var handler = new TestHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

        UpstreamTokenService service = CreateService(handler);

        string token = await service.GetTokenAsync("repository:nginx:pull", CancellationToken.None);

        Assert.Equal("primary-token", token);
    }

    [Fact]
    public async Task GetTokenAsync_AccessToken_FallbackWhenTokenIsNull()
    {
        string json = "{\"access_token\":\"fallback-token\",\"expires_in\":300}";
        var handler = new TestHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

        UpstreamTokenService service = CreateService(handler);

        string token = await service.GetTokenAsync("repository:nginx:pull", CancellationToken.None);

        Assert.Equal("fallback-token", token);
    }

    [Fact]
    public async Task GetTokenAsync_NoToken_ThrowsInvalidOperationException()
    {
        string json = "{\"expires_in\":300}";
        var handler = new TestHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

        UpstreamTokenService service = CreateService(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.GetTokenAsync("repository:nginx:pull", CancellationToken.None));
    }

    [Fact]
    public async Task GetTokenAsync_ExpiresInMinimum_Enforced()
    {
        int callCount = 0;
        var handler = new TestHttpMessageHandler(request =>
        {
            callCount++;
            string json = "{\"token\":\"token-" + callCount + "\",\"expires_in\":10}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });

        var time = new FakeTimeProvider();
        UpstreamTokenService service = CreateService(handler, timeProvider: time);

        await service.GetTokenAsync("repository:nginx:pull", CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(11));

        await service.GetTokenAsync("repository:nginx:pull", CancellationToken.None);

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task GetTokenAsync_NonOkResponse_ThrowsHttpRequestException()
    {
        var handler = new TestHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        UpstreamTokenService service = CreateService(handler);

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await service.GetTokenAsync("repository:nginx:pull", CancellationToken.None));
    }
}
