namespace DockerMirror.UnitTests.TestInfrastructure;

internal sealed class TestHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> factory) : HttpMessageHandler
{
    public TestHttpMessageHandler(HttpResponseMessage fixedResponse)
        : this(_ => fixedResponse)
    {
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(factory(request));
    }
}
