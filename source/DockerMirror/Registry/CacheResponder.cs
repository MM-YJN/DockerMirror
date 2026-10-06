using DockerMirror.Caching;
using DockerMirror.Configuration;
using DockerMirror.Diagnostics;

using Microsoft.Extensions.Options;

namespace DockerMirror.Registry;

// Shared cache I/O: reading, writing, tee-streaming, LRU touch, and tag pointer
// management. Injected into DigestResourceHandler and TagManifestHandler so that
// both benefit from identical cache-hit/miss mechanics without duplication.
internal sealed partial class CacheResponder(
    IContentStore store,
    ITagPointerStore tagStore,
    MirrorMetrics metrics,
    TimeProvider timeProvider,
    IOptions<MirrorOptions> options,
    ILogger<CacheResponder> logger)
{
    // Try to serve a full GET response from cache.
    // On hit: sets Docker-Content-Digest (using digestHeaderValue when provided,
    // otherwise falling back to cached metadata), records metrics, touches the entry,
    // and returns an IResult. Returns null on cache miss.
    // Caller is responsible for logging the hit or miss.
    internal async ValueTask<IResult?> TryServeCachedAsync(
        string cacheKey, string? digestHeaderValue, string resourceType,
        string? rangeHeader, HttpResponse response, ResponseCachePolicy policy,
        CancellationToken ct)
    {
        if (rangeHeader is not null && store is IRangeContentStore rangeStore)
        {
            return await TryServeCachedRangeAsync(rangeStore, cacheKey, digestHeaderValue, resourceType, rangeHeader, response, policy, ct).ConfigureAwait(false);
        }

        CachedContent? cached = await store.TryGetAsync(cacheKey, ct).ConfigureAwait(false);

        if (cached is null)
        {
            return null;
        }

        string? header = digestHeaderValue ?? (string.IsNullOrEmpty(cached.Digest) ? null : cached.Digest);

        if (header is not null)
        {
            if (options.Value.Cache.Headers.Enabled)
            {
                ConditionalRequest.ApplyValidatorHeaders(
                    response, ConditionalRequest.FormatETag(header), header, policy.BuildCacheControl(),
                    cached.StoredAtUtc, timeProvider);
            }
            else
            {
                response.Headers[RegistryResponses.DockerContentDigest] = header;
            }
        }

        metrics.RecordCacheHit(resourceType, "get");
        metrics.RecordServedBytes(cached.Length, "cache", resourceType);
        await TouchOnHitAsync(cacheKey, ct).ConfigureAwait(false);
        return Results.Stream(cached.Stream, cached.ContentType, enableRangeProcessing: cached.Stream.CanSeek);
    }

    // Try to serve a HEAD response from cache.
    // On hit: disposes the stream immediately (to avoid leaking file descriptors),
    // sets status/ContentType/ContentLength/Docker-Content-Digest, records metrics,
    // touches the entry, and returns true. Returns false on cache miss.
    // Caller is responsible for logging the hit or miss.
    internal async ValueTask<bool> TryServeHeadAsync(
        string cacheKey, string? digestHeaderValue, string resourceType,
        HttpResponse response, ResponseCachePolicy policy, CancellationToken ct)
    {
        CachedContent? cached = await store.TryGetAsync(cacheKey, ct).ConfigureAwait(false);

        if (cached is null)
        {
            return false;
        }

        // HEAD needs only the metadata — dispose the stream immediately.
        // Without this the FileStream stays open until GC, leaking a file descriptor
        // on every HEAD cache hit.
        await cached.Stream.DisposeAsync().ConfigureAwait(false);

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = cached.ContentType;
        response.ContentLength = cached.Length;

        string? header = digestHeaderValue ?? (string.IsNullOrEmpty(cached.Digest) ? null : cached.Digest);

        if (header is not null)
        {
            if (options.Value.Cache.Headers.Enabled)
            {
                ConditionalRequest.ApplyValidatorHeaders(
                    response, ConditionalRequest.FormatETag(header), header, policy.BuildCacheControl(),
                    cached.StoredAtUtc, timeProvider);
            }
            else
            {
                response.Headers[RegistryResponses.DockerContentDigest] = header;
            }
        }

        metrics.RecordCacheHit(resourceType, "head");
        await TouchOnHitAsync(cacheKey, ct).ConfigureAwait(false);
        return true;
    }

    private async ValueTask<IResult?> TryServeCachedRangeAsync(
        IRangeContentStore rangeStore, string cacheKey, string? digestHeaderValue,
        string resourceType, string rangeHeader, HttpResponse response,
        ResponseCachePolicy policy, CancellationToken ct)
    {
        RangedContent? ranged = await rangeStore.TryGetRangeAsync(cacheKey, rangeHeader, ct).ConfigureAwait(false);

        if (ranged is null)
        {
            return null;
        }

        try
        {
            string? digestHeader = digestHeaderValue ?? (string.IsNullOrEmpty(ranged.Digest) ? null : ranged.Digest);

            if (digestHeader is not null)
            {
                if (options.Value.Cache.Headers.Enabled)
                {
                    ConditionalRequest.ApplyValidatorHeaders(
                        response, ConditionalRequest.FormatETag(digestHeader), digestHeader, policy.BuildCacheControl(),
                        ranged.StoredAtUtc, timeProvider);
                }
                else
                {
                    response.Headers[RegistryResponses.DockerContentDigest] = digestHeader;
                }
            }

            response.Headers.AcceptRanges = "bytes";

            if (ranged.StatusCode == StatusCodes.Status416RequestedRangeNotSatisfiable)
            {
                response.StatusCode = ranged.StatusCode;

                if (ranged.ContentRange is not null)
                {
                    response.Headers.ContentRange = ranged.ContentRange;
                }

                response.ContentLength = 0;

                metrics.RecordCacheHit(resourceType, "get");
                await TouchOnHitAsync(cacheKey, ct).ConfigureAwait(false);
                return Results.Empty;
            }

            response.StatusCode = ranged.StatusCode;
            response.ContentType = ranged.ContentType;

            if (ranged.ContentRange is not null)
            {
                response.Headers.ContentRange = ranged.ContentRange;
            }

            if (ranged.ContentLength >= 0)
            {
                response.ContentLength = ranged.ContentLength;
            }

            metrics.RecordCacheHit(resourceType, "get");
            metrics.RecordServedBytes(ranged.ContentLength >= 0 ? ranged.ContentLength : 0, "cache", resourceType);
            await TouchOnHitAsync(cacheKey, ct).ConfigureAwait(false);
            await ranged.Stream.CopyToAsync(response.Body, ct).ConfigureAwait(false);
            return Results.Empty;
        }
        finally
        {
            await ranged.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Stream a 200 upstream response to the client while simultaneously writing it to
    // the cache. On successful commit, optionally updates the tag pointer for tagKey.
    // Extracted to avoid duplicating the tee + metrics + tag-pointer logic between
    // DigestResourceHandler and TagManifestHandler.
    internal async Task StreamAndCacheAsync(
        HttpResponseMessage upstreamResponse,
        HttpResponse response,
        string cacheKey,
        Digest digest,
        string resource,
        ResponseCachePolicy policy,
        CancellationToken ct,
        string? tagKey = null,
        bool cacheOnly = false)
    {
        string contentType = upstreamResponse.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        Stream upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        try
        {
            ICacheWriteHandle writeHandle = await store.BeginWriteAsync(cacheKey, digest, ct).ConfigureAwait(false);
            await using (writeHandle.ConfigureAwait(false))
            {
                writeHandle.SetMetadata(new CacheEntryMetadata(contentType));

                // When cacheOnly, the caller will set response status/headers (e.g. 304).
                // Skip writing to response.Body and use Stream.Null so the body is
                // still cached for future requests.
                if (!cacheOnly)
                {
                    ApplyMissResponseHeaders(upstreamResponse, response, digest, contentType, policy);
                }

                Stream outputStream = cacheOnly ? Stream.Null : response.Body;
                (long copied, bool cacheOk) = await ProxyStreaming.TeeStreamAsync(
                    upstreamStream, outputStream, writeHandle.Stream, cacheKey, logger, ct).ConfigureAwait(false);

                metrics.RecordServedBytes(copied, "upstream", resource);

                await CommitAndRecordAsync(writeHandle, digest, resource, copied, cacheOk, tagKey, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            await upstreamStream.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Copy all safe upstream headers so the miss response is header-equivalent to
    // subsequent cache hits, then overwrite the two headers we own (Docker-Content-Digest,
    // status) and, when header synthesis is enabled, ETag/Cache-Control/Last-Modified.
    private void ApplyMissResponseHeaders(
        HttpResponseMessage upstreamResponse, HttpResponse response,
        Digest digest, string contentType, ResponseCachePolicy policy)
    {
        UpstreamRegistryClient.CopyResponseHeaders(upstreamResponse, response);
        response.ContentType = contentType;
        response.Headers[RegistryResponses.DockerContentDigest] = digest.Canonical;
        response.StatusCode = StatusCodes.Status200OK;

        // Overwrite upstream ETag/Cache-Control with our synthesized headers
        // so cache-hit and cache-miss responses use consistent validators.
        if (options.Value.Cache.Headers.Enabled)
        {
            ConditionalRequest.ApplyValidatorHeaders(
                response, ConditionalRequest.ToETag(digest), digest.Canonical,
                policy.BuildCacheControl(), storedAtUtc: null, timeProvider);
        }
    }

    // Commit the write handle on a successful tee, record cache-write metrics, and
    // update the tag pointer on success. Called from StreamAndCacheAsync so the tee
    // path stays linear.
    private async Task CommitAndRecordAsync(
        ICacheWriteHandle writeHandle, Digest digest, string resource,
        long copied, bool cacheOk, string? tagKey, CancellationToken ct)
    {
        if (!cacheOk)
        {
            metrics.RecordCacheWrite(resource, committed: false, bytes: 0);
            return;
        }

        bool committed = await writeHandle.CommitAsync(ct).ConfigureAwait(false);

        if (committed)
        {
            LogCacheWriteSuccess(digest.Canonical);
            metrics.RecordCacheWrite(resource, committed: true, copied);

            if (tagKey is not null)
            {
                await TryUpdateTagPointerAsync(tagKey, digest, timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
            }
        }
        else
        {
            metrics.RecordCacheWrite(resource, committed: false, bytes: 0);
        }
    }

    internal ValueTask<TagPointer?> TryGetTagAsync(string tagKey, CancellationToken ct)
        => tagStore.TryGetTagAsync(tagKey, ct);

    // Check whether a body is cached for the given key without consuming the stream.
    // Used by TagManifestHandler to decide whether a conditional GET (with If-None-Match)
    // can be sent upstream — we only conditional-GET when the body is already cached.
    internal async ValueTask<bool> IsBodyCachedAsync(string cacheKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cacheKey);
        return await store.ExistsAsync(cacheKey, ct).ConfigureAwait(false);
    }

    internal async Task TryUpdateTagPointerAsync(
        string tagKey, Digest digest, DateTimeOffset resolvedAt, CancellationToken ct)
    {
        try
        {
            await tagStore.SetTagAsync(tagKey, new TagPointer(digest, resolvedAt), ct).ConfigureAwait(false);
            LogTagPointerWritten(digest.Canonical, tagKey);
        }
        catch (Exception ex)
        {
            LogTagPointerWriteFailed(ex, tagKey);
        }
    }

    internal async Task TouchOnHitAsync(string cacheKey, CancellationToken ct)
    {
        CacheEvictionOptions eviction = options.Value.Cache.Eviction;

        if (!eviction.Enabled || eviction.Strategy != EvictionStrategy.Lru || store is not ICacheMaintenance maintenance)
        {
            return;
        }

        try
        {
            await maintenance.TouchAsync(cacheKey, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogTouchFailed(ex, cacheKey);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache write succeeded for digest {Digest}.")]
    private partial void LogCacheWriteSuccess(string digest);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to refresh cache timestamp for LRU eviction: {CacheKey}.")]
    private partial void LogTouchFailed(Exception ex, string cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Tag pointer written for digest {Digest} -> tag key {TagKey}.")]
    private partial void LogTagPointerWritten(string digest, string tagKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to write tag pointer for key {TagKey}.")]
    private partial void LogTagPointerWriteFailed(Exception ex, string tagKey);
}
