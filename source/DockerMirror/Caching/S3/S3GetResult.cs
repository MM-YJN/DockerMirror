using System.Net;

namespace DockerMirror.Caching.S3;

internal sealed class S3GetResult(HttpResponseMessage response, Stream stream, long contentLength, string contentType, string digest, DateTimeOffset? fetchedAt, HttpStatusCode statusCode, string? contentRange) : IAsyncDisposable, IDisposable
{
    public Stream Stream => stream;

    public long ContentLength { get; } = contentLength;

    public string ContentType { get; } = contentType;

    public string Digest { get; } = digest;

    public DateTimeOffset? FetchedAt { get; } = fetchedAt;

    public HttpStatusCode StatusCode { get; } = statusCode;

    public string? ContentRange { get; } = contentRange;

    public void Dispose()
    {
        stream.Dispose();
        response.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await stream.DisposeAsync().ConfigureAwait(false);
        response.Dispose();
    }
}
