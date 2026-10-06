using System.Collections.Concurrent;

namespace DockerMirror.E2ETests.TestInfrastructure;

internal sealed class RecordingHandler(ConcurrentDictionary<string, int> counter) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string path = request.RequestUri?.AbsolutePath ?? string.Empty;
        counter.AddOrUpdate(path, 1, (_, count) => count + 1);

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
