using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;

namespace DockerMirror.Diagnostics;

/// <summary>
/// Central metrics for DockerMirror. Owns a single <see cref="Meter"/> and exposes
/// intent-revealing <c>Record*</c>/<c>Update*</c> methods rather than raw instruments.
/// Following the "few instruments, rich tags" pattern, a small number of counters and
/// histograms are multiplexed across many logical events via discriminator tags
/// (<c>result</c>, <c>resource</c>, <c>method</c>, <c>outcome</c>, ...).
/// </summary>
/// <remarks>
/// Uses only the BCL <see cref="System.Diagnostics.Metrics"/> API so it stays
/// AOT-compatible and adds no package dependencies. Observe with any
/// <see cref="MeterListener"/> (e.g. <c>dotnet-counters</c>) via the meter name
/// <see cref="MeterName"/>.
/// </remarks>
[ExcludeFromCodeCoverage]
public sealed class MirrorMetrics : IDisposable
{
    /// <summary>The meter name to subscribe to (e.g. with <c>dotnet-counters</c>).</summary>
    public const string MeterName = "DockerMirror";

    private readonly Meter _meter;

    internal Meter Meter => _meter;

    private readonly Counter<long> _cacheRequests;
    private readonly Counter<long> _cacheWrites;
    private readonly Counter<long> _cacheWriteBytes;
    private readonly Counter<long> _servedBytes;
    private readonly Counter<long> _negativeCacheEvents;
    private readonly Counter<long> _upstreamRequests;
    private readonly Histogram<double> _upstreamRequestDuration;
    private readonly Counter<long> _tokenFetches;
    private readonly Counter<long> _evictions;
    private readonly Counter<long> _evictedBytes;
    private readonly Histogram<double> _sweepDuration;
    private readonly Counter<long> _warmOperations;
    private readonly Counter<long> _warmEnqueued;
    private readonly Counter<long> _lockContended;
    private readonly Counter<long> _responsesNotModified;

    // Backing fields for the cache-size observable gauges. Pushed from the eviction
    // sweep via UpdateCacheSize and pulled by the gauge callbacks.
    private long _cacheSizeBytes;
    private long _cacheEntryCount;

    public MirrorMetrics()
    {
        _meter = new Meter(MeterName);

        _cacheRequests = _meter.CreateCounter<long>(
            "dockermirror.cache.requests",
            description: "Cache lookups tagged by result (hit/miss), resource and method.");

        _cacheWrites = _meter.CreateCounter<long>(
            "dockermirror.cache.writes",
            description: "Cache write attempts tagged by result (committed/failed) and resource.");

        _cacheWriteBytes = _meter.CreateCounter<long>(
            "dockermirror.cache.write.bytes",
            unit: "By",
            description: "Bytes committed to the content cache.");

        _servedBytes = _meter.CreateCounter<long>(
            "dockermirror.served.bytes",
            unit: "By",
            description: "Bytes streamed to clients tagged by source (cache/upstream) and resource.");

        _negativeCacheEvents = _meter.CreateCounter<long>(
            "dockermirror.negative_cache.events",
            description: "Negative-cache events tagged by result (hit/store) and resource.");

        _upstreamRequests = _meter.CreateCounter<long>(
            "dockermirror.upstream.requests",
            description: "Requests sent to the upstream registry tagged by method, status and outcome.");

        _upstreamRequestDuration = _meter.CreateHistogram<double>(
            "dockermirror.upstream.request.duration",
            unit: "s",
            description: "Duration of upstream registry requests in seconds.");

        _tokenFetches = _meter.CreateCounter<long>(
            "dockermirror.upstream.token.fetches",
            description: "Bearer tokens fetched from the upstream auth service (cache misses).");

        _evictions = _meter.CreateCounter<long>(
            "dockermirror.cache.evictions",
            description: "Cache entries evicted tagged by reason (max_age/max_size).");

        _evictedBytes = _meter.CreateCounter<long>(
            "dockermirror.cache.evicted.bytes",
            unit: "By",
            description: "Bytes reclaimed by cache eviction sweeps.");

        _sweepDuration = _meter.CreateHistogram<double>(
            "dockermirror.cache.sweep.duration",
            unit: "s",
            description: "Duration of cache eviction sweeps in seconds.");

        _warmOperations = _meter.CreateCounter<long>(
            "dockermirror.warm.operations",
            description: "Background cache-warm operations tagged by result (succeeded/failed).");

        _warmEnqueued = _meter.CreateCounter<long>(
            "dockermirror.warm.enqueued",
            description: "Cache-warm enqueue attempts tagged by result (accepted/rejected).");

        _lockContended = _meter.CreateCounter<long>(
            "dockermirror.cache.lock.contended",
            description: "Number of times a per-key cache lock was contended.");

        _responsesNotModified = _meter.CreateCounter<long>(
            "dockermirror.responses.not_modified",
            description: "304 Not Modified responses served by resource type.");

        _meter.CreateObservableGauge(
            "dockermirror.cache.size.bytes",
            () => Interlocked.Read(ref _cacheSizeBytes),
            unit: "By",
            description: "Total size of cached content as of the last eviction sweep.");

        _meter.CreateObservableGauge(
            "dockermirror.cache.size.entries",
            () => Interlocked.Read(ref _cacheEntryCount),
            description: "Number of cached entries as of the last eviction sweep.");
    }

    internal void RecordCacheHit(string resource, string method)
        => _cacheRequests.Add(1, Tag("result", "hit"), Tag("resource", resource), Tag("method", method));

    internal void RecordCacheMiss(string resource, string method)
        => _cacheRequests.Add(1, Tag("result", "miss"), Tag("resource", resource), Tag("method", method));

    internal void RecordCacheWrite(string resource, bool committed, long bytes)
    {
        _cacheWrites.Add(1, Tag("result", committed ? "committed" : "failed"), Tag("resource", resource));
        if (committed && bytes > 0)
        {
            _cacheWriteBytes.Add(bytes, Tag("resource", resource));
        }
    }

    internal void RecordServedBytes(long bytes, string source, string resource)
    {
        if (bytes > 0)
        {
            _servedBytes.Add(bytes, Tag("source", source), Tag("resource", resource));
        }
    }

    internal void RecordNegativeCacheHit(string resource)
        => _negativeCacheEvents.Add(1, Tag("result", "hit"), Tag("resource", resource));

    internal void RecordNegativeCacheStore(string resource)
        => _negativeCacheEvents.Add(1, Tag("result", "store"), Tag("resource", resource));

    internal void RecordUpstreamRequest(string method, int statusCode, double durationSeconds)
    {
        KeyValuePair<string, object?> methodTag = Tag("method", method);
        _upstreamRequests.Add(
            1,
            methodTag,
            Tag("status", statusCode),
            Tag("outcome", ClassifyOutcome(statusCode)));
        _upstreamRequestDuration.Record(durationSeconds, methodTag);
    }

    internal void RecordTokenFetch() => _tokenFetches.Add(1);

    internal void RecordEviction(string reason) => _evictions.Add(1, Tag("reason", reason));

    internal void RecordEvictedBytes(long bytes)
    {
        if (bytes > 0)
        {
            _evictedBytes.Add(bytes);
        }
    }

    internal void RecordSweepDuration(double seconds) => _sweepDuration.Record(seconds);

    internal void UpdateCacheSize(long bytes, long entries)
    {
        Interlocked.Exchange(ref _cacheSizeBytes, bytes);
        Interlocked.Exchange(ref _cacheEntryCount, entries);
    }

    internal void RecordWarm(bool succeeded)
        => _warmOperations.Add(1, Tag("result", succeeded ? "succeeded" : "failed"));

    internal void RecordWarmEnqueue(bool accepted)
        => _warmEnqueued.Add(1, Tag("result", accepted ? "accepted" : "rejected"));

    internal void RecordLockContended() => _lockContended.Add(1);

    internal void RecordResponseNotModified(string resource)
        => _responsesNotModified.Add(1, Tag("resource", resource));

    internal void RegisterActiveLocksGauge(Func<long> observe)
        => _meter.CreateObservableGauge(
            "dockermirror.cache.locks.active",
            observe,
            description: "Number of live per-key cache locks.");

    internal void RegisterNegativeCacheGauge(Func<long> observe)
        => _meter.CreateObservableGauge(
            "dockermirror.negative_cache.entries",
            observe,
            description: "Number of entries currently in the negative cache.");

    internal void RegisterListCacheGauge(string name, Func<long> observe)
        => _meter.CreateObservableGauge(
            name,
            observe,
            description: "Number of entries currently in the list response cache.");

    internal void RegisterWarmQueueGauge(Func<long> observe)
        => _meter.CreateObservableGauge(
            "dockermirror.warm.queue.inflight",
            observe,
            description: "Number of in-flight background cache-warm requests.");

    public void Dispose() => _meter.Dispose();

    private static string ClassifyOutcome(int statusCode) => statusCode switch
    {
        0 => "error",
        304 => "not_modified",
        >= 500 => "server_error",
        >= 400 => "client_error",
        >= 300 => "redirect",
        _ => "success",
    };

    private static KeyValuePair<string, object?> Tag(string key, object? value) => new(key, value);
}
