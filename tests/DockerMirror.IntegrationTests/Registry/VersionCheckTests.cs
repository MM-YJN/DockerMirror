using System.Net;

using DockerMirror.IntegrationTests.TestInfrastructure;

namespace DockerMirror.IntegrationTests.Registry;

public sealed class VersionCheckTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task GetVersionCheck_Returns200WithApiVersionHeader()
    {
        await using var factory = new MirrorTestFactory(testOutputHelper);

        using HttpClient client = factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        HttpResponseMessage response = await client.GetAsync("/v2/", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("Docker-Distribution-Api-Version"));
        Assert.Contains("registry/2.0", response.Headers.GetValues("Docker-Distribution-Api-Version"));
    }

    [Fact]
    public async Task HeadVersionCheck_Returns200WithApiVersionHeader()
    {
        await using var factory = new MirrorTestFactory(testOutputHelper);

        using HttpClient client = factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        using var request = new HttpRequestMessage(HttpMethod.Head, "/v2/");
        HttpResponseMessage response = await client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("Docker-Distribution-Api-Version"));
        Assert.Contains("registry/2.0", response.Headers.GetValues("Docker-Distribution-Api-Version"));
        Assert.Equal(0, response.Content.Headers.ContentLength ?? 0);
    }

    [Fact]
    public async Task PostVersionCheck_Returns405MethodNotAllowed()
    {
        await using var factory = new MirrorTestFactory(testOutputHelper);

        using HttpClient client = factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        HttpResponseMessage response = await client.PostAsync("/v2/", null, cancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }
}
