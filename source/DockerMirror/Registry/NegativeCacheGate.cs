using System.Net;

using DockerMirror.Caching;
using DockerMirror.Configuration;
using DockerMirror.Diagnostics;

using Microsoft.Extensions.Options;

namespace DockerMirror.Registry;

// Encapsulates all negative-cache interaction: enabled check, key construction,
// hit-and-serve, and conditional store. Replaces the BuildKey/TryGet/Store
// triple repeated throughout the registry service.
internal sealed partial class NegativeCacheGate(
    NegativeCache negativeCache,
    IOptions<MirrorOptions> options,
    MirrorMetrics metrics,
    ILogger<NegativeCacheGate> logger)
{
    internal bool Enabled => options.Value.Cache.Enabled && options.Value.Cache.NegativeCache.Enabled;

    // Returns true when a negative-cache entry exists for the given resource, recording the
    // hit metric and log. Does not write any HTTP response; use TryServe404Async for that.
    internal bool IsNegativeHit(string rewrittenName, string resourceType, string reference)
    {
        if (!Enabled)
        {
            return false;
        }

        string negKey = NegativeCache.BuildKey(rewrittenName, resourceType, reference);

        if (!negativeCache.TryGet(negKey))
        {
            return false;
        }

        LogNegativeCacheHit(negKey);
        metrics.RecordNegativeCacheHit(resourceType);
        return true;
    }

    // Returns true when a negative-cache entry was found and the 404 response was written.
    internal async Task<bool> TryServe404Async(
        string rewrittenName, string resourceType, string reference,
        HttpResponse response, bool writeBody, CancellationToken ct)
    {
        if (!IsNegativeHit(rewrittenName, resourceType, reference))
        {
            return false;
        }

        await RegistryResponses.WriteNegative404Async(resourceType, response, writeBody, ct).ConfigureAwait(false);
        return true;
    }

    // Stores a negative-cache entry when the upstream status code warrants it.
    // No-op when disabled or when the status code is not 404/410.
    internal void StoreIfNegative(
        HttpStatusCode status, string rewrittenName, string resourceType, string reference)
    {
        if (!Enabled)
        {
            return;
        }

        if (status is not (HttpStatusCode.NotFound or HttpStatusCode.Gone))
        {
            return;
        }

        string negKey = NegativeCache.BuildKey(rewrittenName, resourceType, reference);
        negativeCache.Store(negKey, options.Value.Cache.NegativeCache.Ttl);
        LogNegativeCacheStore(negKey);
        metrics.RecordNegativeCacheStore(resourceType);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Negative cache hit for {Key}.")]
    private partial void LogNegativeCacheHit(string key);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Negative cache store for {Key}.")]
    private partial void LogNegativeCacheStore(string key);
}
