using System.Net;
using System.Text.Json;

using DockerMirror.Configuration;
using DockerMirror.IntegrationTests.TestInfrastructure;

using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DockerMirror.IntegrationTests.Admin;

public sealed class AdminStatsTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task GetAdminStats_WhenEnabled_Returns200Json()
    {
        await using var factory = new MirrorTestFactory(testOutputHelper, builder => builder.ConfigureTestServices(services => services.Configure<MirrorOptions>(o => o.Admin = new AdminOptions { Enabled = true })));
        using HttpClient client = factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        HttpResponseMessage response = await client.GetAsync("/admin/stats", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        Assert.True(root.TryGetProperty("version", out _));
        Assert.True(root.TryGetProperty("uptime", out _));

        Assert.True(root.TryGetProperty("upstream", out JsonElement upstream));
        Assert.True(upstream.TryGetProperty("registryUrl", out _));
        Assert.True(upstream.TryGetProperty("tokenService", out _));
        Assert.True(upstream.TryGetProperty("defaultNamespace", out _));

        Assert.True(root.TryGetProperty("cache", out JsonElement cache));
        Assert.True(cache.TryGetProperty("enabled", out _));
        Assert.True(cache.TryGetProperty("backend", out _));
        Assert.True(cache.TryGetProperty("evictionEnabled", out _));
        Assert.True(cache.TryGetProperty("negativeCacheEnabled", out _));
        Assert.True(cache.TryGetProperty("tagManifests", out JsonElement tagManifests));
        Assert.True(tagManifests.TryGetProperty("enabled", out _));
        Assert.True(tagManifests.TryGetProperty("ttl", out _));
        Assert.True(cache.TryGetProperty("sizeBytes", out _));
        Assert.True(cache.TryGetProperty("entryCount", out _));
        Assert.True(cache.TryGetProperty("lastUpdatedUtc", out _));
        Assert.True(cache.TryGetProperty("negativeCacheEntries", out _));

        Assert.True(root.TryGetProperty("storage", out JsonElement storage));
        Assert.True(storage.TryGetProperty("healthy", out _));
        Assert.True(storage.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task GetAdminStats_WhenDisabled_Returns404()
    {
        await using var factory = new MirrorTestFactory(testOutputHelper);

        using HttpClient client = factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        HttpResponseMessage response = await client.GetAsync("/admin/stats", cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PostAdminStats_WhenEnabled_Returns405()
    {
        await using var factory = new MirrorTestFactory(testOutputHelper, builder => builder.ConfigureTestServices(services => services.Configure<MirrorOptions>(o => o.Admin = new AdminOptions { Enabled = true })));
        using HttpClient client = factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        HttpResponseMessage response = await client.PostAsync("/admin/stats", null, cancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }
}
