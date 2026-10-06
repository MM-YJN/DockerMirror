namespace DockerMirror.Registry;

// Shared HTTP response constants and helpers for the registry v2 protocol.
internal static class RegistryResponses
{
    internal const string DockerDistributionApiVersion = "Docker-Distribution-Api-Version";
    internal const string DockerDistributionApiVersionValue = "registry/2.0";
    internal const string DockerContentDigest = "Docker-Content-Digest";

    private static readonly byte[] s_unsupported400Body =
        "{\"errors\":[{\"code\":\"UNSUPPORTED\",\"message\":\"Invalid path format\"}]}"u8.ToArray();

    private static readonly byte[] s_manifestUnknown404Body =
        "{\"errors\":[{\"code\":\"MANIFEST_UNKNOWN\",\"message\":\"manifest unknown\"}]}"u8.ToArray();

    private static readonly byte[] s_blobUnknown404Body =
        "{\"errors\":[{\"code\":\"BLOB_UNKNOWN\",\"message\":\"blob unknown\"}]}"u8.ToArray();

    internal static async Task WriteBadPathAsync(HttpResponse response, CancellationToken ct)
    {
        response.StatusCode = StatusCodes.Status400BadRequest;
        response.ContentType = "application/json";
        response.Headers[DockerDistributionApiVersion] = DockerDistributionApiVersionValue;
        await response.Body.WriteAsync(s_unsupported400Body, ct).ConfigureAwait(false);
    }

    internal static async Task WriteNegative404Async(
        string resourceType, HttpResponse response, bool writeBody, CancellationToken ct)
    {
        response.StatusCode = StatusCodes.Status404NotFound;
        response.Headers[DockerDistributionApiVersion] = DockerDistributionApiVersionValue;

        if (writeBody)
        {
            response.ContentType = "application/json";
            byte[] body = resourceType == "manifests" ? s_manifestUnknown404Body : s_blobUnknown404Body;
            await response.Body.WriteAsync(body, ct).ConfigureAwait(false);
        }
    }

    // Extract the Docker-Content-Digest header value from an HttpResponseMessage.
    // Returns null if the header is absent.
    internal static string? TryParseDockerContentDigest(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues(DockerContentDigest, out IEnumerable<string>? values))
        {
            return values.FirstOrDefault();
        }

        return null;
    }
}
