using System.Diagnostics;
using System.Net;

using DockerMirror.Caching;
using DockerMirror.Configuration;
using DockerMirror.Diagnostics;

using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace DockerMirror.Registry;

// Handles GET and HEAD requests for content-addressed resources (blobs and manifests
// by digest). Single-flight on miss via KeyedAsyncLock; CDN blob redirects followed
// through the "upstream-redirect" named HttpClient.
internal sealed partial class DigestResourceHandler(
    CacheResponder cache,
    NegativeCacheGate negGate,
    UpstreamRegistryClient upstream,
    KeyedAsyncLock keyedLock,
    CacheWarmingQueue warmingQueue,
    IHttpClientFactory httpClientFactory,
    IOptions<MirrorOptions> options,
    TimeProvider timeProvider,
    MirrorMetrics metrics,
    ILogger<DigestResourceHandler> logger)
{
    internal async Task<IResult> HandleGetAsync(
        RegistryPath registryPath, Digest digest, string cacheKey,
        HttpRequest request, HttpResponse response, CancellationToken ct)
    {
        var immutablePolicy = new ResponseCachePolicy(Immutable: true, options.Value.Cache.Headers.ImmutableMaxAge);

        // Digest-addressed resources are content-addressable: if the client already
        // holds the exact digest, we can return 304 without consulting cache/upstream.
        // This is provably correct because the digest *is* the content identity.
        if (options.Value.Cache.Headers.Enabled &&
            ConditionalRequest.IfNoneMatchMatches(request, ConditionalRequest.ToETag(digest)))
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            ConditionalRequest.ApplyValidatorHeaders(
                response, ConditionalRequest.ToETag(digest), digest.Canonical,
                immutablePolicy.BuildCacheControl(), storedAtUtc: null, timeProvider);
            metrics.RecordResponseNotModified(registryPath.ResourceType);
            return Results.Empty;
        }

        IResult? result = await cache.TryServeCachedAsync(
            cacheKey, null, registryPath.ResourceType,
            request.Headers.Range.Count > 0 ? request.Headers.Range.ToString() : null,
            response, immutablePolicy, ct).ConfigureAwait(false);

        if (result is not null)
        {
            LogCacheHit(digest.Canonical);
            return result;
        }

        string rewrittenName = registryPath.RewrittenName(options.Value.Upstream.DefaultNamespace);

        if (await negGate.TryServe404Async(
            rewrittenName, registryPath.ResourceType, digest.Canonical, response, writeBody: true, ct).ConfigureAwait(false))
        {
            return Results.Empty;
        }

        if (request.Headers.Range.Count > 0)
        {
            // Range request on miss: passthrough, enqueue background warm.
            await upstream.ForwardParsedAsync(registryPath, request, response, ct).ConfigureAwait(false);
            warmingQueue.TryEnqueue(new WarmRequest(rewrittenName, registryPath.ResourceType, digest));
            return Results.Empty;
        }

        // Full GET miss: single-flight, fetch, tee, commit.
        return await HandleFullGetMissAsync(
            registryPath, rewrittenName, digest, cacheKey, request, response, ct).ConfigureAwait(false);
    }

    private async Task<IResult> HandleFullGetMissAsync(
        RegistryPath registryPath, string rewrittenName, Digest digest, string cacheKey,
        HttpRequest request, HttpResponse response, CancellationToken ct)
    {
        using IDisposable releaser = await keyedLock.LockAsync(cacheKey, ct).ConfigureAwait(false);

        var immutablePolicy = new ResponseCachePolicy(Immutable: true, options.Value.Cache.Headers.ImmutableMaxAge);

        // Double-check cache after acquiring the lock; a concurrent request may have
        // already populated the entry.
        IResult? result = await cache.TryServeCachedAsync(
            cacheKey, null, registryPath.ResourceType, null, response, immutablePolicy, ct).ConfigureAwait(false);

        if (result is not null)
        {
            LogCacheHitAfterLock(digest.Canonical);
            return result;
        }

        if (await negGate.TryServe404Async(
            rewrittenName, registryPath.ResourceType, digest.Canonical, response, writeBody: true, ct).ConfigureAwait(false))
        {
            return Results.Empty;
        }

        LogCacheMiss(digest.Canonical);
        metrics.RecordCacheMiss(registryPath.ResourceType, "get");

        HttpResponseMessage upstreamResponse;

        if (registryPath.ResourceType == "blobs")
        {
            upstreamResponse = await FetchBlobWithRedirectAsync(registryPath, ct).ConfigureAwait(false);
        }
        else
        {
            // Forward the client's Accept header so the registry returns the correct manifest
            // type (e.g. OCI index vs. legacy manifest). Fall back to standard defaults.
            StringValues acceptValues = request.Headers["Accept"];
            IEnumerable<string> acceptMediaTypes = acceptValues.Count > 0
                ? acceptValues
                : UpstreamRegistryClient.s_defaultManifestAcceptTypes;

            upstreamResponse = await upstream.SendUpstreamAsync(
                registryPath, HttpMethod.Get, acceptMediaTypes, ct).ConfigureAwait(false);
        }

        try
        {
            if (upstreamResponse.StatusCode != HttpStatusCode.OK)
            {
                negGate.StoreIfNegative(upstreamResponse.StatusCode, rewrittenName, registryPath.ResourceType, digest.Canonical);

                response.StatusCode = (int)upstreamResponse.StatusCode;
                UpstreamRegistryClient.CopyResponseHeaders(upstreamResponse, response);
                await upstreamResponse.Content.CopyToAsync(response.Body, ct).ConfigureAwait(false);
                return Results.Empty;
            }

            await cache.StreamAndCacheAsync(
                upstreamResponse, response, cacheKey, digest, registryPath.ResourceType, immutablePolicy, ct).ConfigureAwait(false);
            return Results.Empty;
        }
        finally
        {
            upstreamResponse.Dispose();
        }
    }

    internal async Task<IResult> HandleHeadAsync(
        RegistryPath registryPath, Digest digest, string cacheKey,
        HttpRequest request, HttpResponse response, CancellationToken ct)
    {
        var immutablePolicy = new ResponseCachePolicy(Immutable: true, options.Value.Cache.Headers.ImmutableMaxAge);

        // Digest-addressed resources: if the client already holds the exact digest,
        // return 304 without consulting cache/upstream.
        if (options.Value.Cache.Headers.Enabled &&
            ConditionalRequest.IfNoneMatchMatches(request, ConditionalRequest.ToETag(digest)))
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            ConditionalRequest.ApplyValidatorHeaders(
                response, ConditionalRequest.ToETag(digest), digest.Canonical,
                immutablePolicy.BuildCacheControl(), storedAtUtc: null, timeProvider);
            metrics.RecordResponseNotModified(registryPath.ResourceType);
            return Results.Empty;
        }

        if (await cache.TryServeHeadAsync(
            cacheKey, null, registryPath.ResourceType, response, immutablePolicy, ct).ConfigureAwait(false))
        {
            LogCacheHit(digest.Canonical);
            return Results.Empty;
        }

        string rewrittenName = registryPath.RewrittenName(options.Value.Upstream.DefaultNamespace);

        if (await negGate.TryServe404Async(
            rewrittenName, registryPath.ResourceType, digest.Canonical, response, writeBody: false, ct).ConfigureAwait(false))
        {
            return Results.Empty;
        }

        // HEAD miss: send HEAD upstream so the status is observable for negative caching.
        IEnumerable<string>? acceptMediaTypes = null;

        if (registryPath.ResourceType == "manifests")
        {
            StringValues acceptValues = request.Headers["Accept"];
            acceptMediaTypes = acceptValues.Count > 0
                ? (IEnumerable<string>)acceptValues
                : UpstreamRegistryClient.s_defaultManifestAcceptTypes;
        }

        using HttpResponseMessage headResponse = await upstream.SendUpstreamAsync(
            registryPath, HttpMethod.Head, acceptMediaTypes, ct).ConfigureAwait(false);

        negGate.StoreIfNegative(headResponse.StatusCode, rewrittenName, registryPath.ResourceType, digest.Canonical);

        response.StatusCode = (int)headResponse.StatusCode;
        UpstreamRegistryClient.CopyResponseHeaders(headResponse, response);
        return Results.Empty;
    }

    private async Task<HttpResponseMessage> FetchBlobWithRedirectAsync(
        RegistryPath registryPath, CancellationToken ct)
    {
        // Blobs are opaque bytes; no Accept header required.
        HttpResponseMessage response = await upstream.SendUpstreamAsync(
            registryPath, HttpMethod.Get, null, ct).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Redirect or
            HttpStatusCode.MovedPermanently or
            HttpStatusCode.RedirectMethod or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect)
        {
            Uri? location = response.Headers.Location;
            response.Dispose();

            if (location is not null)
            {
                HttpClient redirectClient = httpClientFactory.CreateClient("upstream-redirect");
                return await redirectClient.GetAsync(
                    location, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        return response;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache hit for digest {Digest}.")]
    private partial void LogCacheHit(string digest);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache hit (after lock) for digest {Digest}.")]
    private partial void LogCacheHitAfterLock(string digest);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache miss for digest {Digest}.")]
    private partial void LogCacheMiss(string digest);
}
