using System.Net.Http.Headers;

namespace DockerMirror.E2ETests.TestInfrastructure;

internal static class RegistrySeeder
{
    public static async Task<SeededImage> SeedAsync(string registryBaseUrl, string repository, string tag, CancellationToken ct)
    {
        RegistryImageFactory.ImageBuildResult result = RegistryImageFactory.Build(repository, tag);

        using var client = new HttpClient { BaseAddress = new Uri(registryBaseUrl.TrimEnd('/') + "/") };

        ct.ThrowIfCancellationRequested();

        // Push config blob.
        await PushBlobAsync(client, repository, result.ConfigJsonBytes, result.Image.ConfigDigest, ct).ConfigureAwait(false);

        // Push layer blob.
        await PushBlobAsync(client, repository, result.LayerGzipBytes, result.Image.LayerDigest, ct).ConfigureAwait(false);

        // Push manifest.
        await PushManifestAsync(client, result.Image, result.ManifestJsonBytes, ct).ConfigureAwait(false);

        return result.Image;
    }

    private static async Task PushBlobAsync(HttpClient client, string repository, byte[] blob, string digest, CancellationToken ct)
    {
        // POST to start upload.
        using HttpResponseMessage startResponse = await client.PostAsync($"v2/{repository}/blobs/uploads/", null, ct).ConfigureAwait(false);
        startResponse.EnsureSuccessStatusCode();

        Uri location = startResponse.Headers.Location
            ?? throw new InvalidOperationException("No Location header in blob upload response.");

        // Registry may return a relative or absolute URL. Make it absolute against client.BaseAddress.
        if (!location.IsAbsoluteUri)
        {
            location = new Uri(client.BaseAddress!, location);
        }

        // Append digest; handle existing query string.
        string separator = location.Query.Length > 0 ? "&" : "?";
        string finalUrl = $"{location.GetLeftPart(UriPartial.Path)}{location.Query}{separator}digest={Uri.EscapeDataString(digest)}";

        using var content = new ByteArrayContent(blob);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using HttpResponseMessage putResponse = await client.PutAsync(finalUrl, content, ct).ConfigureAwait(false);
        putResponse.EnsureSuccessStatusCode();
    }

    private static async Task PushManifestAsync(HttpClient client, SeededImage image, byte[] manifestBytes, CancellationToken ct)
    {
        string url = $"v2/{image.Repository}/manifests/{image.Tag}";

        using var content = new ByteArrayContent(manifestBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.docker.distribution.manifest.v2+json");

        using HttpResponseMessage response = await client.PutAsync(url, content, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}
