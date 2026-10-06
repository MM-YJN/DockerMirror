using System.Diagnostics;

using DockerMirror.Caching;
using DockerMirror.Configuration;

using Microsoft.Extensions.Options;

namespace DockerMirror.Registry;

// Thin routing layer: parses the registry path, determines cacheability, and
// dispatches to DigestResourceHandler, TagManifestHandler, or upstream passthrough.
internal sealed class CachingRegistryService(
    IOptions<MirrorOptions> options,
    UpstreamRegistryClient upstream,
    DigestResourceHandler digestHandler,
    TagManifestHandler tagHandler,
    ListResourceHandler listHandler)
{
    public async Task<IResult> HandleAsync(string path, HttpContext context, CancellationToken ct)
    {
        HttpRequest request = context.Request;
        HttpResponse response = context.Response;

        RegistryPath registryPath;
        try
        {
            registryPath = RegistryPath.Parse(path);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            await RegistryResponses.WriteBadPathAsync(response, ct).ConfigureAwait(false);
            return Results.Empty;
        }

        CacheOptions cache = options.Value.Cache;
        bool cachingEnabled = cache.Enabled;
        bool isGetOrHead = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);
        bool isDigest = registryPath.Reference is not null && Digest.IsDigest(registryPath.Reference);
        bool isCacheableResource = registryPath.ResourceType is "blobs" or "manifests";
        bool cacheTagManifests = cache.TagManifests.Enabled;

        // Tag manifest path: mutable tag references resolved through TagManifestHandler.
        if (cachingEnabled && cacheTagManifests &&
            isGetOrHead && registryPath.ResourceType == "manifests" && !isDigest && registryPath.Reference is not null)
        {
            if (HttpMethods.IsGet(request.Method))
            {
                return await tagHandler.HandleGetAsync(registryPath, request, response, ct).ConfigureAwait(false);
            }

            return await tagHandler.HandleHeadAsync(registryPath, request, response, ct).ConfigureAwait(false);
        }

        // Tags/list path: cached with short TTL via ListResourceHandler.
        if (cachingEnabled && cache.TagsList.Enabled &&
            isGetOrHead && registryPath.ResourceType == "tags" && registryPath.Reference == "list")
        {
            string rewrittenName = registryPath.RewrittenName(options.Value.Upstream.DefaultNamespace);
            return await listHandler.HandleTagsListAsync(rewrittenName, request, response, ct).ConfigureAwait(false);
        }

        // Digest path: content-addressed blobs and manifests served through DigestResourceHandler.
        if (!cachingEnabled || !isGetOrHead || !isDigest || !isCacheableResource)
        {
            await upstream.ForwardParsedAsync(registryPath, request, response, ct).ConfigureAwait(false);
            return Results.Empty;
        }

        Debug.Assert(registryPath.Reference is not null, "Reference should be non-null for digest cache operations.");
        string reference = registryPath.Reference!;
        var digest = Digest.Parse(reference);
        string cacheKey = DigestCacheKey.FromDigest(digest);

        if (HttpMethods.IsGet(request.Method))
        {
            return await digestHandler.HandleGetAsync(
                registryPath, digest, cacheKey, request, response, ct).ConfigureAwait(false);
        }

        return await digestHandler.HandleHeadAsync(
            registryPath, digest, cacheKey, request, response, ct).ConfigureAwait(false);
    }
}
