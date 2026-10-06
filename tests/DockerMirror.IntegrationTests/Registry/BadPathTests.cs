using System.Net;

using DockerMirror.IntegrationTests.TestInfrastructure;

namespace DockerMirror.IntegrationTests.Registry;

public sealed class BadPathTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task BadPath_NoResourceType_Returns400WithError()
    {
        await using var factory = new MirrorTestFactory(testOutputHelper);

        using HttpClient client = factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        HttpResponseMessage response = await client.GetAsync("/v2/no-resource-type", cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.Contains("Docker-Distribution-Api-Version"));

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.Contains("UNSUPPORTED", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BadPath_OnlyResourceType_Returns400WithError()
    {
        await using var factory = new MirrorTestFactory(testOutputHelper);

        using HttpClient client = factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        HttpResponseMessage response = await client.GetAsync("/v2/manifests/latest", cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.Contains("UNSUPPORTED", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BadPath_EmptyPath_ReturnsVersionCheck()
    {
        await using var factory = new MirrorTestFactory(testOutputHelper);

        using HttpClient client = factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        HttpResponseMessage response = await client.GetAsync("/v2/", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("Docker-Distribution-Api-Version"));
    }

    [Fact]
    public async Task ValidPath_Get_ReturnsOk()
    {
        await using var factory = new MirrorTestFactory(testOutputHelper);

        factory.Handler.SetupToken(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"token\":\"ok\",\"expires_in\":300}", System.Text.Encoding.UTF8, "application/json"),
            });

        factory.Handler.SetupRegistry(request =>
            new HttpResponseMessage(HttpStatusCode.OK));

        using HttpClient client = factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        HttpResponseMessage response = await client.GetAsync("/v2/mariadb/manifests/latest", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ReferrersPath_NotBadPath_ReturnsUpstreamResponse()
    {
        await using var factory = new MirrorTestFactory(testOutputHelper);

        factory.Handler.SetupToken(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"token\":\"ok\",\"expires_in\":300}", System.Text.Encoding.UTF8, "application/json"),
            });

        factory.Handler.SetupRegistry(request =>
            new HttpResponseMessage(HttpStatusCode.OK));

        using HttpClient client = factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        HttpResponseMessage response = await client.GetAsync("/v2/nginx/referrers/sha256:abc", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CatalogPath_NotBadPath_ReturnsUpstreamResponse()
    {
        await using var factory = new MirrorTestFactory(testOutputHelper);

        factory.Handler.SetupToken(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"token\":\"ok\",\"expires_in\":300}", System.Text.Encoding.UTF8, "application/json"),
            });

        factory.Handler.SetupRegistry(request =>
            new HttpResponseMessage(HttpStatusCode.OK));

        using HttpClient client = factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        HttpResponseMessage response = await client.GetAsync("/v2/_catalog", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
