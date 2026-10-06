using System.Net;
using System.Text;

namespace DockerMirror.IntegrationTests.TestInfrastructure;

public sealed class TestDelegatingHandler : DelegatingHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = [];
    private readonly List<HttpRequestMessage> _allRequests = [];

    public IReadOnlyList<HttpRequestMessage> AllRequests => _allRequests;

    public void Setup(string pathPrefix, Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        _routes[pathPrefix] = handler;
    }

    public void SetupToken(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        Setup("/token", handler);
    }

    public void SetupRegistry(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        Setup("/v2/", handler);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _allRequests.Add(request);
        string path = request.RequestUri?.PathAndQuery
            ?? throw new InvalidOperationException("RequestUri must be set.");

        foreach ((string? prefix, Func<HttpRequestMessage, HttpResponseMessage>? handler) in _routes)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal))
            {
                return Task.FromResult(handler(request));
            }
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"No handler registered for {path}", Encoding.UTF8, "text/plain"),
        });
    }
}
