using System.Net;
using System.Text;

using DockerMirror.Configuration;
using DockerMirror.IntegrationTests.TestInfrastructure;

using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DockerMirror.IntegrationTests.Registry;

public sealed class BasePathIntegrationTests(ITestOutputHelper testOutputHelper)
{
    private MirrorTestFactory CreateFactoryWithBasePath(string? basePath, bool adminEnabled = false)
    {
        return new MirrorTestFactory(
            testOutputHelper,
            cacheEnabled: true,
            negativeCacheEnabled: true,
            configureBuilder: builder => builder.ConfigureTestServices(services =>
            {
                services.Configure<MirrorOptions>(o => o.BasePath = basePath);
                if (adminEnabled)
                {
                    services.Configure<MirrorOptions>(o => o.Admin = new AdminOptions { Enabled = true });
                }
            }));
    }

    [Fact]
    public async Task GetVersionCheck_WithBasePath_Returns200()
    {
        await using MirrorTestFactory factory = CreateFactoryWithBasePath("/docker");
        using HttpClient client = factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        HttpResponseMessage response = await client.GetAsync("/docker/v2/", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("Docker-Distribution-Api-Version"));
        Assert.Contains("registry/2.0", response.Headers.GetValues("Docker-Distribution-Api-Version"));
    }

    [Fact]
    public async Task GetVersionCheck_WithBasePath_RootReturns404()
    {
        await using MirrorTestFactory factory = CreateFactoryWithBasePath("/docker");
        using HttpClient client = factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        HttpResponseMessage response = await client.GetAsync("/v2/", cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Catalog_WithBasePath_ForwardsCorrectly()
    {
        await using MirrorTestFactory factory = CreateFactoryWithBasePath("/docker");

        factory.Handler.SetupToken(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"token\":\"test-token\",\"expires_in\":300}",
                    Encoding.UTF8,
                    "application/json"),
            });

        HttpRequestMessage? lastRegistryRequest = null;
        factory.Handler.SetupRegistry(request =>
        {
            lastRegistryRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"repositories\":[\"a\",\"b\"]}",
                    Encoding.UTF8,
                    "application/json"),
            };
        });

        using HttpClient client = factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        HttpResponseMessage response = await client.GetAsync("/docker/v2/_catalog", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(lastRegistryRequest);
        Assert.Equal("/v2/_catalog", lastRegistryRequest.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task AdminStats_WithBasePath_Enabled()
    {
        await using MirrorTestFactory factory = CreateFactoryWithBasePath("/docker", adminEnabled: true);
        using HttpClient client = factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        HttpResponseMessage response = await client.GetAsync("/docker/admin/stats", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task AdminStats_WithBasePath_RootReturns404()
    {
        await using MirrorTestFactory factory = CreateFactoryWithBasePath("/docker", adminEnabled: true);
        using HttpClient client = factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        HttpResponseMessage response = await client.GetAsync("/admin/stats", cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task HealthLive_WithBasePath_StaysAtRoot()
    {
        await using MirrorTestFactory factory = CreateFactoryWithBasePath("/docker");
        using HttpClient client = factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        // MirrorTestFactory doesn't mock the upstream health check, so /health/ready may
        // fail, but /health/live (process-only) should always succeed.
        HttpResponseMessage response = await client.GetAsync("/health/live", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HealthReady_WithBasePath_Not404()
    {
        await using MirrorTestFactory factory = CreateFactoryWithBasePath("/docker");
        using HttpClient client = factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        HttpResponseMessage response = await client.GetAsync("/health/ready", cancellationToken);

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetVersionCheck_NoBasePath_StillWorks()
    {
        await using var factory = new MirrorTestFactory(testOutputHelper);
        using HttpClient client = factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        HttpResponseMessage response = await client.GetAsync("/v2/", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("Docker-Distribution-Api-Version"));
        Assert.Contains("registry/2.0", response.Headers.GetValues("Docker-Distribution-Api-Version"));
    }
}
