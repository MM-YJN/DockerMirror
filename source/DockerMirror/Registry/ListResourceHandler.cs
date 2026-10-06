using System.Net;

using DockerMirror.Caching;
using DockerMirror.Configuration;
using DockerMirror.Diagnostics;

using Microsoft.Extensions.Options;

namespace DockerMirror.Registry;

// Handles GET and HEAD requests for /v2/{name}/tags/list and /v2/_catalog with a
// short-TTL in-memory cache (ListResponseCache). Cache keys include the full query
// string so paginated responses are preserved. Mixed-safe: non-200 responses are
// passed through and never cached.
internal sealed partial class ListResourceHandler(
    UpstreamRegistryClient upstream,
    KeyedAsyncLock keyedLock,
    IOptions<MirrorOptions> options,
    TimeProvider timeProvider,
    TagsListResponseCache tagsListCache,
    CatalogListResponseCache catalogListCache,
    MirrorMetrics metrics,
    ILogger<ListResourceHandler> logger)
{
    private const string TagsListPrefix = "tags:";
    private const string CatalogPrefix = "catalog:";
    private const string LockPrefix = "list:";

    internal async Task<IResult> HandleTagsListAsync(
        string rewrittenName, HttpRequest request, HttpResponse response, CancellationToken ct)
    {
        ListCacheOptions listOptions = options.Value.Cache.TagsList;
        if (!options.Value.Cache.Enabled || !listOptions.Enabled)
        {
            await upstream.ForwardRawAsync($"v2/{rewrittenName}/tags/list", request, response, ct).ConfigureAwait(false);
            return Results.Empty;
        }

        string queryString = request.QueryString.Value ?? string.Empty;
        string path = $"v2/{rewrittenName}/tags/list{queryString}";
        string cacheKey = $"{TagsListPrefix}{rewrittenName}{queryString}";

        return await HandleListAsync(
            path, cacheKey, listOptions, tagsListCache, "tags-list", request, response, ct).ConfigureAwait(false);
    }

    internal async Task<IResult> HandleCatalogAsync(
        HttpRequest request, HttpResponse response, CancellationToken ct)
    {
        ListCacheOptions listOptions = options.Value.Cache.Catalog;
        if (!options.Value.Cache.Enabled || !listOptions.Enabled)
        {
            await upstream.ForwardRawAsync("v2/_catalog", request, response, ct).ConfigureAwait(false);
            return Results.Empty;
        }

        string queryString = request.QueryString.Value ?? string.Empty;
        string path = $"v2/_catalog{queryString}";
        string cacheKey = $"{CatalogPrefix}{queryString}";

        return await HandleListAsync(
            path, cacheKey, listOptions, catalogListCache, "catalog", request, response, ct).ConfigureAwait(false);
    }

    private async Task<IResult> HandleListAsync(
        string upstreamPath, string cacheKey, ListCacheOptions listOptions,
        ListResponseCache listCache, string resource,
        HttpRequest request, HttpResponse response, CancellationToken ct)
    {
        if (HttpMethods.IsHead(request.Method))
        {
            return await HandleHeadListAsync(cacheKey, listOptions, listCache, upstreamPath, response, ct).ConfigureAwait(false);
        }

        var listPolicy = new ResponseCachePolicy(Immutable: false, listOptions.Ttl);

        // Fresh cache hit — serve without locking.
        IResult? fresh = await TryServeFreshListHitAsync(
            cacheKey, listCache, resource, request, response, listPolicy, ct).ConfigureAwait(false);
        if (fresh is not null)
        {
            return fresh;
        }

        // Cache miss — single-flight fetch.
        string lockKey = $"{LockPrefix}{cacheKey}";
        using IDisposable releaser = await keyedLock.LockAsync(lockKey, ct).ConfigureAwait(false);

        // Double-check after lock.
        fresh = await TryServeFreshListHitAsync(
            cacheKey, listCache, resource, request, response, listPolicy, ct).ConfigureAwait(false);
        if (fresh is not null)
        {
            return fresh;
        }

        LogListCacheMiss(cacheKey);
        metrics.RecordCacheMiss(resource, "get");

        // If a stale entry exists (expired but still in dictionary), use its ETag for
        // upstream conditional revalidation to avoid re-fetching unchanged content.
        string? staleETag = null;
        ListResponseCache.Entry? staleEntry = null;
        if (listCache.TryGetExpiredEntry(cacheKey, out ListResponseCache.Entry? expiredEntry) && expiredEntry.ETag is not null)
        {
            staleETag = expiredEntry.ETag;
            staleEntry = expiredEntry;
        }

        using HttpResponseMessage upstreamResponse = await upstream.SendRawAsync(
            upstreamPath, HttpMethod.Get, ifNoneMatch: staleETag, ct).ConfigureAwait(false);

        // Upstream 304 — content unchanged, refresh and serve cached.
        if (upstreamResponse.StatusCode == HttpStatusCode.NotModified && staleEntry is not null)
        {
            return await HandleUpstreamNotModifiedAsync(
                cacheKey, listCache, listOptions, staleEntry, resource,
                request, response, listPolicy, ct).ConfigureAwait(false);
        }

        if (upstreamResponse.StatusCode != HttpStatusCode.OK)
        {
            // Non-200: passthrough, do not cache.
            return await PassThroughAsync(upstreamResponse, response, ct).ConfigureAwait(false);
        }

        string contentType = upstreamResponse.Content.Headers.ContentType?.ToString() ?? "application/json";
        string? upstreamETag = upstreamResponse.Headers.ETag?.ToString();
        string? upstreamLink = upstreamResponse.Headers.TryGetValues("Link", out IEnumerable<string>? linkValues)
            ? string.Join(", ", linkValues)
            : null;

        // Check Content-Length to decide whether to cache.
        long? contentLength = upstreamResponse.Content.Headers.ContentLength;
        if (contentLength.HasValue && listCache.IsBodySizeOversized(contentLength.Value))
        {
            LogListOversized(cacheKey, contentLength.Value);
            return await PassThroughAsync(upstreamResponse, response, ct).ConfigureAwait(false);
        }

        // Buffer the body into a MemoryStream so the cached length always equals
        // the bytes actually received. Content-Length is used as a capacity hint
        // but never dictates the final array size, guarding against upstreams that
        // return fewer bytes than the header claims.
        byte[] bodyBytes = await BufferBodyAsync(upstreamResponse, listOptions, contentLength, ct).ConfigureAwait(false);

        // Re-check size after buffering (if Content-Length was absent or inaccurate).
        if (listCache.IsBodySizeOversized(bodyBytes.Length))
        {
            LogListOversized(cacheKey, bodyBytes.Length);
            return await PassThroughBufferedAsync(upstreamResponse, response, bodyBytes, ct).ConfigureAwait(false);
        }

        // Cache and serve.
        return await CacheAndServeAsync(
            cacheKey, listCache, listOptions, resource, bodyBytes, contentType,
            upstreamETag, upstreamLink, request, response, listPolicy, ct).ConfigureAwait(false);
    }

    // Serves a fresh (unexpired) list cache entry when present: records the hit metric,
    // honours If-None-Match with a 304, otherwise streams the cached body. Returns null
    // when no fresh entry is cached so the caller proceeds to the locked miss path.
    private async Task<IResult?> TryServeFreshListHitAsync(
        string cacheKey, ListResponseCache listCache, string resource,
        HttpRequest request, HttpResponse response, ResponseCachePolicy listPolicy,
        CancellationToken ct)
    {
        if (!listCache.TryGet(cacheKey, out ListResponseCache.Entry? entry))
        {
            return null;
        }

        LogListCacheHit(cacheKey);
        metrics.RecordCacheHit(resource, "get");

        if (options.Value.Cache.Headers.Enabled && entry.ETag is not null &&
            ConditionalRequest.IfNoneMatchMatches(request, entry.ETag))
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            SetListResponseHeaders(response, entry.ETag, listPolicy.BuildCacheControl(), entry.StoredAtUtc, entry.Link);
            metrics.RecordResponseNotModified(resource);
            return Results.Empty;
        }

        await ServeCachedEntryAsync(response, entry, listPolicy, ct).ConfigureAwait(false);
        return Results.Empty;
    }

    // Upstream returned 304 against our stale ETag: refresh the cache entry's TTL
    // (re-storing from the stale body if it was evicted concurrently) and serve it,
    // honouring the client's If-None-Match.
    private async Task<IResult> HandleUpstreamNotModifiedAsync(
        string cacheKey, ListResponseCache listCache, ListCacheOptions listOptions,
        ListResponseCache.Entry staleEntry, string resource,
        HttpRequest request, HttpResponse response, ResponseCachePolicy listPolicy,
        CancellationToken ct)
    {
        // Refresh the cache entry's TTL. If the entry was evicted concurrently
        // (e.g. by TrimExcess), re-store it from the stale body since upstream
        // confirmed the content is unchanged.
        if (!listCache.RefreshExpiry(cacheKey, listOptions.Ttl))
        {
            listCache.Store(cacheKey, staleEntry with { StoredAtUtc = timeProvider.GetUtcNow() }, listOptions.Ttl);
        }

        DateTimeOffset storedAt = timeProvider.GetUtcNow();

        if (options.Value.Cache.Headers.Enabled && staleEntry.ETag is not null &&
            ConditionalRequest.IfNoneMatchMatches(request, staleEntry.ETag))
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            SetListResponseHeaders(response, staleEntry.ETag, listPolicy.BuildCacheControl(),
                storedAt, staleEntry.Link);
            metrics.RecordResponseNotModified(resource);
            return Results.Empty;
        }

        ListResponseCache.Entry revalidatedEntry = staleEntry with { StoredAtUtc = storedAt };
        await ServeCachedEntryAsync(response, revalidatedEntry, listPolicy, ct).ConfigureAwait(false);
        return Results.Empty;
    }

    // Copy an upstream response straight to the client without caching (non-200 or
    // oversized-by-Content-Length). The upstream body is streamed directly.
    private static async Task<IResult> PassThroughAsync(
        HttpResponseMessage upstreamResponse, HttpResponse response, CancellationToken ct)
    {
        response.StatusCode = (int)upstreamResponse.StatusCode;
        UpstreamRegistryClient.CopyResponseHeaders(upstreamResponse, response);
        await upstreamResponse.Content.CopyToAsync(response.Body, ct).ConfigureAwait(false);
        return Results.Empty;
    }

    // Write already-buffered bytes to the client without caching (oversized discovered
    // after buffering, when Content-Length was absent or inaccurate).
    private static async Task<IResult> PassThroughBufferedAsync(
        HttpResponseMessage upstreamResponse, HttpResponse response,
        byte[] bodyBytes, CancellationToken ct)
    {
        response.StatusCode = (int)upstreamResponse.StatusCode;
        UpstreamRegistryClient.CopyResponseHeaders(upstreamResponse, response);
        await response.Body.WriteAsync(bodyBytes, ct).ConfigureAwait(false);
        return Results.Empty;
    }

    // Buffer the upstream body into a byte[] using Content-Length as a capacity hint
    // (never as the final array size, guarding against upstreams that return fewer
    // bytes than the header claims).
    private static async Task<byte[]> BufferBodyAsync(
        HttpResponseMessage upstreamResponse, ListCacheOptions listOptions,
        long? contentLength, CancellationToken ct)
    {
        Stream bodyStream = await upstreamResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        try
        {
            int capacity = contentLength is { } len && len > 0 && len <= listOptions.MaxBodyBytes
                ? (int)len
                : 0;
            using MemoryStream ms = capacity > 0 ? new MemoryStream(capacity) : new MemoryStream();
            await bodyStream.CopyToAsync(ms, ct).ConfigureAwait(false);
            return ms.ToArray();
        }
        finally
        {
            await bodyStream.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Store the fetched body in the list cache and serve it to the client, honouring
    // the client's If-None-Match with a 304 when headers are enabled.
    private async Task<IResult> CacheAndServeAsync(
        string cacheKey, ListResponseCache listCache, ListCacheOptions listOptions,
        string resource, byte[] bodyBytes, string contentType,
        string? upstreamETag, string? upstreamLink,
        HttpRequest request, HttpResponse response, ResponseCachePolicy listPolicy,
        CancellationToken ct)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        var cacheEntry = new ListResponseCache.Entry
        {
            Body = bodyBytes,
            ContentType = contentType,
            ETag = upstreamETag,
            Link = upstreamLink,
            StoredAtUtc = now,
        };

        listCache.Store(cacheKey, cacheEntry, listOptions.Ttl);

        if (options.Value.Cache.Headers.Enabled && upstreamETag is not null &&
            ConditionalRequest.IfNoneMatchMatches(request, upstreamETag))
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            SetListResponseHeaders(response, upstreamETag, listPolicy.BuildCacheControl(), now, upstreamLink);
            metrics.RecordResponseNotModified(resource);
            return Results.Empty;
        }

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = contentType;
        response.ContentLength = bodyBytes.Length;
        SetListResponseHeaders(response, upstreamETag, listPolicy.BuildCacheControl(), now, upstreamLink);
        await response.Body.WriteAsync(bodyBytes, ct).ConfigureAwait(false);
        return Results.Empty;
    }

    private async Task ServeCachedEntryAsync(
        HttpResponse response, ListResponseCache.Entry entry,
        ResponseCachePolicy listPolicy,
        CancellationToken ct)
    {
        SetListResponseHeaders(response, entry.ETag, listPolicy.BuildCacheControl(), entry.StoredAtUtc, entry.Link);
        response.ContentType = entry.ContentType;
        response.ContentLength = entry.Body.Length;
        response.StatusCode = StatusCodes.Status200OK;
        await response.Body.WriteAsync(entry.Body, ct).ConfigureAwait(false);
    }

    private async Task<IResult> HandleHeadListAsync(
        string cacheKey, ListCacheOptions listOptions, ListResponseCache listCache,
        string upstreamPath,
        HttpResponse response, CancellationToken ct)
    {
        var listPolicy = new ResponseCachePolicy(Immutable: false, listOptions.Ttl);

        if (listCache.TryGet(cacheKey, out ListResponseCache.Entry? entry))
        {
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = entry.ContentType;
            response.ContentLength = entry.Body.Length;
            SetListResponseHeaders(response, entry.ETag, listPolicy.BuildCacheControl(), entry.StoredAtUtc, entry.Link);

            return Results.Empty;
        }

        // No cache entry — passthrough HEAD.
        using HttpResponseMessage headResponse = await upstream.SendRawAsync(upstreamPath, HttpMethod.Head, null, ct).ConfigureAwait(false);
        response.StatusCode = (int)headResponse.StatusCode;
        UpstreamRegistryClient.CopyResponseHeaders(headResponse, response);
        return Results.Empty;
    }

    private void SetListResponseHeaders(
        HttpResponse response, string? etag, string cacheControl,
        DateTimeOffset storedAtUtc, string? link)
    {
        if (options.Value.Cache.Headers.Enabled)
        {
            ConditionalRequest.ApplyValidatorHeaders(
                response, etag, etag ?? string.Empty, cacheControl, storedAtUtc, timeProvider,
                setDockerContentDigest: false);
        }
        else if (etag is not null)
        {
            response.Headers.ETag = etag;
        }

        if (link is not null)
        {
            response.Headers["Link"] = link;
        }
    }

    [LoggerMessage(LogLevel.Debug, Message = "List cache hit for key {CacheKey}.")]
    private partial void LogListCacheHit(string cacheKey);

    [LoggerMessage(LogLevel.Debug, Message = "List cache miss for key {CacheKey}.")]
    private partial void LogListCacheMiss(string cacheKey);

    [LoggerMessage(LogLevel.Debug, Message = "List response oversized for key {CacheKey} ({Size} bytes), not cached.")]
    private partial void LogListOversized(string cacheKey, long size);
}
