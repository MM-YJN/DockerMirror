using System.Diagnostics;
using System.Net;

using DockerMirror.Caching;
using DockerMirror.Configuration;
using DockerMirror.Diagnostics;

using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace DockerMirror.Registry;

// Handles GET and HEAD requests for tag-addressed manifests (mutable references).
// Resolves tags to digests via a pointer store with TTL-based revalidation; the
// actual manifest body is served from the digest-addressed content cache.
internal sealed partial class TagManifestHandler(
    CacheResponder cache,
    NegativeCacheGate negGate,
    UpstreamRegistryClient upstream,
    KeyedAsyncLock keyedLock,
    IOptions<MirrorOptions> options,
    TimeProvider timeProvider,
    MirrorMetrics metrics,
    ILogger<TagManifestHandler> logger)
{
    private readonly record struct TagContext(
        string RewrittenName, string TagKey, IEnumerable<string> AcceptMediaTypes,
        DateTimeOffset Now, TimeSpan TagTtl);

    internal async Task<IResult> HandleGetAsync(
        RegistryPath registryPath, HttpRequest request, HttpResponse response, CancellationToken ct)
    {
        if (request.Headers.Range.Count > 0)
        {
            // Range on a tag manifest: passthrough without caching.
            await upstream.ForwardParsedAsync(registryPath, request, response, ct).ConfigureAwait(false);
            return Results.Empty;
        }

        Debug.Assert(registryPath.Reference is not null, "Reference should be non-null for tag manifest operations.");
        string reference = registryPath.Reference!;

        TagContext ctx = BuildTagContext(registryPath, request);
        bool headersEnabled = options.Value.Cache.Headers.Enabled;
        var mutablePolicy = new ResponseCachePolicy(Immutable: false, options.Value.Cache.TagManifests.Ttl);
        TagPointer? pointer = await cache.TryGetTagAsync(ctx.TagKey, ct).ConfigureAwait(false);

        // Fast path: fresh pointer with a cached body — serve without locking.
        if (pointer is not null && (ctx.Now - pointer.Value.ResolvedAtUtc) <= ctx.TagTtl)
        {
            if (headersEnabled &&
                ConditionalRequest.IfNoneMatchMatches(request, ConditionalRequest.ToETag(pointer.Value.Digest)))
            {
                response.StatusCode = StatusCodes.Status304NotModified;
                ConditionalRequest.ApplyValidatorHeaders(
                    response, ConditionalRequest.ToETag(pointer.Value.Digest), pointer.Value.Digest.Canonical,
                    mutablePolicy.BuildCacheControl(), pointer.Value.ResolvedAtUtc, timeProvider);
                metrics.RecordResponseNotModified("manifests");
                return Results.Empty;
            }

            string bodyKey = DigestCacheKey.FromDigest(pointer.Value.Digest);
            IResult? result = await cache.TryServeCachedAsync(
                bodyKey, pointer.Value.Digest.Canonical, "manifests", null, response, mutablePolicy, ct).ConfigureAwait(false);

            if (result is not null)
            {
                LogTagManifestHit(reference, pointer.Value.Digest.Canonical);
                return result;
            }
        }

        if (await negGate.TryServe404Async(
            ctx.RewrittenName, "manifests", reference, response, writeBody: true, ct).ConfigureAwait(false))
        {
            return Results.Empty;
        }

        // All other cases (stale, no pointer, body evicted) — locked full-resolution path.
        return await HandleFullGetAsync(
            registryPath, ctx, reference, request, response, ct).ConfigureAwait(false);
    }

    private async Task<IResult> HandleFullGetAsync(
        RegistryPath registryPath, TagContext ctx, string reference,
        HttpRequest request, HttpResponse response, CancellationToken ct)
    {
        var mutablePolicy = new ResponseCachePolicy(Immutable: false, options.Value.Cache.TagManifests.Ttl);

        using IDisposable releaser = await keyedLock.LockAsync(ctx.TagKey, ct).ConfigureAwait(false);

        // Refresh the clock after acquiring the lock; a concurrent request may have
        // already resolved and cached the manifest.
        DateTimeOffset now = timeProvider.GetUtcNow();
        TagPointer? pointer = await cache.TryGetTagAsync(ctx.TagKey, ct).ConfigureAwait(false);

        IResult? postLockHit = await TryServePostLockHitAsync(
            ctx, reference, pointer, now, request, response, mutablePolicy, ct).ConfigureAwait(false);
        if (postLockHit is not null)
        {
            return postLockHit;
        }

        if (await negGate.TryServe404Async(
            ctx.RewrittenName, "manifests", reference, response, writeBody: true, ct).ConfigureAwait(false))
        {
            return Results.Empty;
        }

        // Stale pointer revalidation.
        if (pointer is not null && (now - pointer.Value.ResolvedAtUtc) > ctx.TagTtl)
        {
            string staleBodyKey = DigestCacheKey.FromDigest(pointer.Value.Digest);
            Task<IResult?> revalidation;

            if (options.Value.Cache.TagManifests.ConditionalRevalidation &&
                await cache.IsBodyCachedAsync(staleBodyKey, ct).ConfigureAwait(false))
            {
                // Body is cached — send conditional GET to collapse HEAD+GET into one round trip.
                revalidation = RevalidateViaConditionalGetAsync(
                    registryPath, ctx, reference, pointer.Value, staleBodyKey, now,
                    request, response, mutablePolicy, ct);
            }
            else
            {
                // Conditional revalidation disabled or body not cached — fall back to HEAD.
                revalidation = RevalidateViaHeadAsync(
                    registryPath, ctx, reference, pointer.Value, now,
                    request, response, mutablePolicy, ct);
            }

            IResult? revalidated = await revalidation.ConfigureAwait(false);
            if (revalidated is not null)
            {
                return revalidated;
            }
        }

        return await FetchAndCacheFullAsync(
            registryPath, ctx, reference, request, response, mutablePolicy, ct).ConfigureAwait(false);
    }

    // Post-lock double-check: a concurrent request may have resolved and cached the
    // manifest while we waited. Serves a 304 or the cached body when the pointer is
    // fresh. Returns null when there is no fresh pointer or its body is missing, so
    // the caller continues to negative-cache + revalidation.
    private async Task<IResult?> TryServePostLockHitAsync(
        TagContext ctx, string reference, TagPointer? pointer, DateTimeOffset now,
        HttpRequest request, HttpResponse response, ResponseCachePolicy mutablePolicy,
        CancellationToken ct)
    {
        if (pointer is null)
        {
            return null;
        }

        TagPointer p = pointer.Value;
        if ((now - p.ResolvedAtUtc) > ctx.TagTtl)
        {
            return null;
        }

        if (options.Value.Cache.Headers.Enabled &&
            ConditionalRequest.IfNoneMatchMatches(request, ConditionalRequest.ToETag(p.Digest)))
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            ConditionalRequest.ApplyValidatorHeaders(
                response, ConditionalRequest.ToETag(p.Digest), p.Digest.Canonical,
                mutablePolicy.BuildCacheControl(), p.ResolvedAtUtc, timeProvider);
            metrics.RecordResponseNotModified("manifests");
            return Results.Empty;
        }

        string bodyKey = DigestCacheKey.FromDigest(p.Digest);
        IResult? result = await cache.TryServeCachedAsync(
            bodyKey, p.Digest.Canonical, "manifests", null, response, mutablePolicy, ct).ConfigureAwait(false);

        if (result is not null)
        {
            LogTagManifestHitAfterLock(reference, p.Digest.Canonical);
        }

        return result;
    }

    // Stale pointer revalidation via conditional GET (body already cached): send
    // If-None-Match upstream and handle 304 (revalidate), 200 (tag changed), and
    // 404/Gone (negative cache). Returns null to fall through to a full GET when the
    // cached body has been evicted mid-revalidation or upstream returns an unexpected
    // status.
    private async Task<IResult?> RevalidateViaConditionalGetAsync(
        RegistryPath registryPath, TagContext ctx, string reference,
        TagPointer pointer, string staleBodyKey, DateTimeOffset now,
        HttpRequest request, HttpResponse response, ResponseCachePolicy mutablePolicy,
        CancellationToken ct)
    {
        string ifNoneMatchValue = ConditionalRequest.ToETag(pointer.Digest);
        using HttpResponseMessage condResponse = await upstream.SendUpstreamAsync(
            registryPath, HttpMethod.Get, ctx.AcceptMediaTypes, ct,
            ifNoneMatch: ifNoneMatchValue).ConfigureAwait(false);

        if (condResponse.StatusCode == HttpStatusCode.NotModified)
        {
            LogTagManifestRevalidated(reference, pointer.Digest.Canonical);

            // Refresh the tag pointer timestamp so the next request skips revalidation.
            await cache.TryUpdateTagPointerAsync(ctx.TagKey, pointer.Digest, now, ct).ConfigureAwait(false);

            // Check if the client also sent a matching If-None-Match.
            if (options.Value.Cache.Headers.Enabled &&
                ConditionalRequest.IfNoneMatchMatches(request, ifNoneMatchValue))
            {
                response.StatusCode = StatusCodes.Status304NotModified;
                ConditionalRequest.ApplyValidatorHeaders(
                    response, ifNoneMatchValue, pointer.Digest.Canonical,
                    mutablePolicy.BuildCacheControl(), now, timeProvider);
                metrics.RecordResponseNotModified("manifests");
                return Results.Empty;
            }

            // Serve the cached body.
            IResult? result = await cache.TryServeCachedAsync(
                staleBodyKey, pointer.Digest.Canonical, "manifests", null, response, mutablePolicy, ct).ConfigureAwait(false);

            if (result is not null)
            {
                return result;
            }

            // Body was evicted between the pre-check and the serve — fall through to full GET.
            LogTagManifestMiss(reference);
            metrics.RecordCacheMiss("manifests", "get");
            return null;
        }

        if (condResponse.StatusCode == HttpStatusCode.OK)
        {
            // Tag changed — stream and cache the new body.
            string? newDigestStr = RegistryResponses.TryParseDockerContentDigest(condResponse);

            if (newDigestStr is not null && Digest.TryParse(newDigestStr, out Digest newDigest))
            {
                LogTagManifestChanged(reference, pointer.Digest.Canonical, newDigest.Canonical);
                string newBodyKey = DigestCacheKey.FromDigest(newDigest);
                await cache.StreamAndCacheAsync(
                    condResponse, response, newBodyKey, newDigest, "manifests", mutablePolicy, ct,
                    tagKey: ctx.TagKey).ConfigureAwait(false);
                return Results.Empty;
            }

            // No parseable Docker-Content-Digest — transparent passthrough.
            response.StatusCode = (int)condResponse.StatusCode;
            UpstreamRegistryClient.CopyResponseHeaders(condResponse, response);
            await condResponse.Content.CopyToAsync(response.Body, ct).ConfigureAwait(false);
            return Results.Empty;
        }

        if (condResponse.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            negGate.StoreIfNegative(condResponse.StatusCode, ctx.RewrittenName, "manifests", reference);

            response.StatusCode = (int)condResponse.StatusCode;
            UpstreamRegistryClient.CopyResponseHeaders(condResponse, response);
            return Results.Empty;
        }

        // Other non-OK/non-304: fall through to full GET below.
        return null;
    }

    // Stale pointer revalidation via HEAD (conditional revalidation disabled or body
    // not cached): resolve the current digest, refresh the pointer, and serve the
    // cached body or a client 304. Returns null to fall through to a full GET when the
    // body is missing; serves a 404/Gone passthrough directly.
    private async Task<IResult?> RevalidateViaHeadAsync(
        RegistryPath registryPath, TagContext ctx, string reference,
        TagPointer pointer, DateTimeOffset now,
        HttpRequest request, HttpResponse response, ResponseCachePolicy mutablePolicy,
        CancellationToken ct)
    {
        using HttpResponseMessage headResponse = await upstream.SendUpstreamAsync(
            registryPath, HttpMethod.Head, ctx.AcceptMediaTypes, ct).ConfigureAwait(false);

        if (RegistryResponses.TryParseDockerContentDigest(headResponse) is { } headDigestStr &&
            Digest.TryParse(headDigestStr, out Digest currentDigest))
        {
            if (currentDigest == pointer.Digest)
            {
                LogTagManifestRevalidated(reference, currentDigest.Canonical);
            }
            else
            {
                LogTagManifestChanged(reference, pointer.Digest.Canonical, currentDigest.Canonical);
            }

            string bodyKey = DigestCacheKey.FromDigest(currentDigest);

            if (options.Value.Cache.Headers.Enabled &&
                ConditionalRequest.IfNoneMatchMatches(request, ConditionalRequest.ToETag(currentDigest)))
            {
                await cache.TryUpdateTagPointerAsync(ctx.TagKey, currentDigest, now, ct).ConfigureAwait(false);

                response.StatusCode = StatusCodes.Status304NotModified;
                ConditionalRequest.ApplyValidatorHeaders(
                    response, ConditionalRequest.ToETag(currentDigest), currentDigest.Canonical,
                    mutablePolicy.BuildCacheControl(), now, timeProvider);
                metrics.RecordResponseNotModified("manifests");
                return Results.Empty;
            }

            IResult? cachedResult = await cache.TryServeCachedAsync(
                bodyKey, currentDigest.Canonical, "manifests", null, response, mutablePolicy, ct).ConfigureAwait(false);

            if (cachedResult is not null)
            {
                await cache.TryUpdateTagPointerAsync(ctx.TagKey, currentDigest, now, ct).ConfigureAwait(false);
                return cachedResult;
            }

            return null;
        }

        if (headResponse.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            negGate.StoreIfNegative(headResponse.StatusCode, ctx.RewrittenName, "manifests", reference);

            response.StatusCode = (int)headResponse.StatusCode;
            UpstreamRegistryClient.CopyResponseHeaders(headResponse, response);
            return Results.Empty;
        }

        return null;
    }

    // Full GET miss: fetch the manifest fresh from upstream, negative-cache non-OK
    // responses, parse the resolved digest, and either stream-and-cache the body or
    // honour a client If-None-Match with a cache-only 304.
    private async Task<IResult> FetchAndCacheFullAsync(
        RegistryPath registryPath, TagContext ctx, string reference,
        HttpRequest request, HttpResponse response, ResponseCachePolicy mutablePolicy,
        CancellationToken ct)
    {
        LogTagManifestMiss(reference);
        metrics.RecordCacheMiss("manifests", "get");

        HttpResponseMessage upstreamResponse = await upstream.SendUpstreamAsync(
            registryPath, HttpMethod.Get, ctx.AcceptMediaTypes, ct,
            queryString: request.QueryString.Value).ConfigureAwait(false);

        try
        {
            if (upstreamResponse.StatusCode != HttpStatusCode.OK)
            {
                negGate.StoreIfNegative(upstreamResponse.StatusCode, ctx.RewrittenName, "manifests", reference);

                response.StatusCode = (int)upstreamResponse.StatusCode;
                UpstreamRegistryClient.CopyResponseHeaders(upstreamResponse, response);
                await upstreamResponse.Content.CopyToAsync(response.Body, ct).ConfigureAwait(false);
                return Results.Empty;
            }

            string? resolvedDigestStr = RegistryResponses.TryParseDockerContentDigest(upstreamResponse);

            if (resolvedDigestStr is null || !Digest.TryParse(resolvedDigestStr, out Digest resolvedDigest))
            {
                // No parseable Docker-Content-Digest — transparent passthrough.
                response.StatusCode = (int)upstreamResponse.StatusCode;
                UpstreamRegistryClient.CopyResponseHeaders(upstreamResponse, response);
                await upstreamResponse.Content.CopyToAsync(response.Body, ct).ConfigureAwait(false);
                return Results.Empty;
            }

            string bodyCacheKey = DigestCacheKey.FromDigest(resolvedDigest);

            if (options.Value.Cache.Headers.Enabled &&
                ConditionalRequest.IfNoneMatchMatches(request, ConditionalRequest.ToETag(resolvedDigest)))
            {
                // Client already holds this exact content. Cache the body for future
                // requests but avoid streaming it over the wire.
                await cache.StreamAndCacheAsync(
                    upstreamResponse, response, bodyCacheKey, resolvedDigest, "manifests",
                    mutablePolicy, ct, tagKey: ctx.TagKey, cacheOnly: true).ConfigureAwait(false);

                response.StatusCode = StatusCodes.Status304NotModified;
                ConditionalRequest.ApplyValidatorHeaders(
                    response, ConditionalRequest.ToETag(resolvedDigest), resolvedDigest.Canonical,
                    mutablePolicy.BuildCacheControl(), storedAtUtc: null, timeProvider);
                metrics.RecordResponseNotModified("manifests");
                return Results.Empty;
            }

            await cache.StreamAndCacheAsync(
                upstreamResponse, response, bodyCacheKey, resolvedDigest, "manifests", mutablePolicy, ct,
                tagKey: ctx.TagKey).ConfigureAwait(false);
            return Results.Empty;
        }
        finally
        {
            upstreamResponse.Dispose();
        }
    }

    internal async Task<IResult> HandleHeadAsync(
        RegistryPath registryPath, HttpRequest request, HttpResponse response, CancellationToken ct)
    {
        TagContext ctx = BuildTagContext(registryPath, request);
        bool headersEnabled = options.Value.Cache.Headers.Enabled;
        var mutablePolicy = new ResponseCachePolicy(Immutable: false, options.Value.Cache.TagManifests.Ttl);
        TagPointer? pointer = await cache.TryGetTagAsync(ctx.TagKey, ct).ConfigureAwait(false);

        if (pointer is not null && (ctx.Now - pointer.Value.ResolvedAtUtc) <= ctx.TagTtl)
        {
            if (headersEnabled &&
                ConditionalRequest.IfNoneMatchMatches(request, ConditionalRequest.ToETag(pointer.Value.Digest)))
            {
                response.StatusCode = StatusCodes.Status304NotModified;
                ConditionalRequest.ApplyValidatorHeaders(
                    response, ConditionalRequest.ToETag(pointer.Value.Digest), pointer.Value.Digest.Canonical,
                    mutablePolicy.BuildCacheControl(), pointer.Value.ResolvedAtUtc, timeProvider);
                metrics.RecordResponseNotModified("manifests");
                return Results.Empty;
            }

            // Fresh pointer — try to serve HEAD from the digest cache.
            string bodyKey = DigestCacheKey.FromDigest(pointer.Value.Digest);

            if (await cache.TryServeHeadAsync(
                bodyKey, pointer.Value.Digest.Canonical, "manifests", response, mutablePolicy, ct).ConfigureAwait(false))
            {
                return Results.Empty;
            }
        }

        // Stale, no pointer, or body missing — send HEAD upstream.
        // Only upsert the pointer when absent or stale so we do not perpetually
        // refresh a fresh pointer whose body was independently evicted.
        bool shouldUpsert = pointer is null || (ctx.Now - pointer.Value.ResolvedAtUtc) > ctx.TagTtl;

        Debug.Assert(registryPath.Reference is not null, "Reference should be non-null for tag manifest operations.");
        string reference = registryPath.Reference!;

        if (await negGate.TryServe404Async(
            ctx.RewrittenName, "manifests", reference, response, writeBody: false, ct).ConfigureAwait(false))
        {
            return Results.Empty;
        }

        HttpResponseMessage headResponse = await upstream.SendUpstreamAsync(
            registryPath, HttpMethod.Head, ctx.AcceptMediaTypes, ct).ConfigureAwait(false);

        try
        {
            string? headDigestStr = RegistryResponses.TryParseDockerContentDigest(headResponse);

            if (shouldUpsert && headDigestStr is not null && Digest.TryParse(headDigestStr, out Digest headDigest))
            {
                // Upsert the pointer so the containerd HEAD-tag-then-GET-digest flow
                // primes the tag mapping for subsequent requests.
                await cache.TryUpdateTagPointerAsync(ctx.TagKey, headDigest, ctx.Now, ct).ConfigureAwait(false);
            }

            negGate.StoreIfNegative(headResponse.StatusCode, ctx.RewrittenName, "manifests", reference);

            response.StatusCode = (int)headResponse.StatusCode;
            UpstreamRegistryClient.CopyResponseHeaders(headResponse, response);
            return Results.Empty;
        }
        finally
        {
            headResponse.Dispose();
        }
    }

    private TagContext BuildTagContext(RegistryPath registryPath, HttpRequest request)
    {
        Debug.Assert(registryPath.Reference is not null, "Reference should be non-null for tag manifest operations.");
        string reference = registryPath.Reference!;

        string rewrittenName = registryPath.RewrittenName(options.Value.Upstream.DefaultNamespace);
        StringValues acceptValues = request.Headers["Accept"];
        IEnumerable<string> acceptMediaTypes = acceptValues.Count > 0
            ? (IEnumerable<string>)acceptValues
            : UpstreamRegistryClient.s_defaultManifestAcceptTypes;
        string acceptKey = string.Join(",", acceptValues.ToArray());
        string tagKey = TagCacheKey.From(rewrittenName, reference, acceptKey);
        DateTimeOffset now = timeProvider.GetUtcNow();
        TimeSpan tagTtl = options.Value.Cache.TagManifests.Ttl;

        return new TagContext(rewrittenName, tagKey, acceptMediaTypes, now, tagTtl);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Tag manifest cache hit for tag {Tag} -> digest {Digest}.")]
    private partial void LogTagManifestHit(string tag, string digest);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Tag manifest cache hit (after lock) for tag {Tag} -> digest {Digest}.")]
    private partial void LogTagManifestHitAfterLock(string tag, string digest);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Tag manifest cache miss for tag {Tag}.")]
    private partial void LogTagManifestMiss(string tag);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Tag manifest revalidated: tag {Tag}, digest unchanged {Digest}.")]
    private partial void LogTagManifestRevalidated(string tag, string digest);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tag manifest digest changed: tag {Tag}, old={OldDigest}, new={NewDigest}.")]
    private partial void LogTagManifestChanged(string tag, string oldDigest, string newDigest);
}
