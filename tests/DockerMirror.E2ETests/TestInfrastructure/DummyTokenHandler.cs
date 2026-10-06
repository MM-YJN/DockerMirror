using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace DockerMirror.E2ETests.TestInfrastructure;

internal sealed class DummyTokenHandler : HttpMessageHandler
{
    private static readonly byte[] s_responseBytes = Encoding.UTF8.GetBytes(
        """{"token":"e2e","access_token":"e2e","expires_in":300}""");

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(s_responseBytes)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/json") },
            },
        });
    }
}
