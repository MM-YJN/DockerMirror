using System.Collections.Concurrent;
using System.Threading.Channels;

using DockerMirror.Configuration;
using DockerMirror.Diagnostics;

using Microsoft.Extensions.Options;

namespace DockerMirror.Caching;

internal sealed class CacheWarmingQueue
{
    private readonly Channel<WarmRequest> _channel;
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);
    private readonly MirrorMetrics _metrics;

    public CacheWarmingQueue(IOptions<MirrorOptions> cacheOptions, MirrorMetrics metrics)
    {
        _metrics = metrics;
        int capacity = cacheOptions.Value.Cache.WarmQueueCapacity;

        // Use Wait mode so that TryWrite returns *false* when the channel is at
        // capacity.  This is critical for the _inFlight guard: a false return lets
        // TryEnqueue remove the guard entry so the digest can be re-enqueued once the
        // channel drains.
        //
        // Do NOT use DropWrite here: despite its name, that mode makes TryWrite return
        // *true* even when the item is silently discarded, which would leave a stale
        // entry in _inFlight and permanently block future warm-ups for that digest.
        var options = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = true,
        };
        _channel = Channel.CreateBounded<WarmRequest>(options);
        _metrics.RegisterWarmQueueGauge(() => InFlightCount);
    }

    public ChannelReader<WarmRequest> Reader => _channel.Reader;

    // Number of digests currently queued or being warmed; surfaced as an observable gauge.
    internal int InFlightCount => _inFlight.Count;

    public bool TryEnqueue(in WarmRequest req)
    {
        string digestKey = req.Digest.Canonical;

        // Guard against in-flight duplicates.  Only attempt a channel write when
        // this digest is not already being warmed.
        if (!_inFlight.TryAdd(digestKey, 0))
        {
            _metrics.RecordWarmEnqueue(accepted: false);
            return false;
        }

        // With BoundedChannelFullMode.Wait, TryWrite returns false when the channel is
        // at capacity without blocking.  Remove the in-flight guard on false so the
        // digest can be re-enqueued once the channel drains.
        if (!_channel.Writer.TryWrite(req))
        {
            _inFlight.TryRemove(digestKey, out _);
            _metrics.RecordWarmEnqueue(accepted: false);
            return false;
        }

        _metrics.RecordWarmEnqueue(accepted: true);
        return true;
    }

    public void MarkDone(string digestKey) => _inFlight.TryRemove(digestKey, out _);
}
