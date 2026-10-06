using System.Net;

namespace DockerMirror.Caching.S3;

internal sealed class S3Exception : Exception
{
    public HttpStatusCode StatusCode { get; }

    public S3Exception() { }

    public S3Exception(string message) : base(message) { }

    public S3Exception(string message, Exception innerException) : base(message, innerException) { }

    public S3Exception(HttpStatusCode statusCode, string message)
        : base($"S3 request failed with status {(int)statusCode}: {message}")
        => StatusCode = statusCode;
}
