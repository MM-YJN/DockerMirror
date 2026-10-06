using DockerMirror.Caching;
using DockerMirror.Configuration;
using DockerMirror.Diagnostics;

using Microsoft.Extensions.Options;

namespace DockerMirror.UnitTests.Caching;

public sealed class CacheWarmingQueueTests : IDisposable
{
    private readonly MirrorMetrics _metrics = new();

    public void Dispose() => _metrics.Dispose();
    private static WarmRequest MakeRequest(string hexSuffix = "")
    {
        string hex = new string('a', 63) + (string.IsNullOrEmpty(hexSuffix) ? "0" : hexSuffix[0]);
        var digest = new Digest("sha256", hex);
        return new WarmRequest("library/nginx", "blobs", digest);
    }

    [Fact]
    public void TryEnqueue_SameDigest_SecondReturnsFalse()
    {
        var queue = new CacheWarmingQueue(Options.Create(new MirrorOptions { Cache = new CacheOptions { WarmQueueCapacity = 10 } }), _metrics);
        WarmRequest req = MakeRequest();

        Assert.True(queue.TryEnqueue(req));
        Assert.False(queue.TryEnqueue(req)); // duplicate while in-flight
    }

    [Fact]
    public void TryEnqueue_AfterMarkDone_AllowsReenqueue()
    {
        var queue = new CacheWarmingQueue(Options.Create(new MirrorOptions { Cache = new CacheOptions { WarmQueueCapacity = 10 } }), _metrics);
        WarmRequest req = MakeRequest();

        Assert.True(queue.TryEnqueue(req));
        queue.MarkDone(req.Digest.Canonical);

        // After the worker signals done the same digest can be re-enqueued.
        Assert.True(queue.TryEnqueue(req));
    }

    [Fact]
    public void TryEnqueue_DifferentDigests_BothSucceed()
    {
        var queue = new CacheWarmingQueue(Options.Create(new MirrorOptions { Cache = new CacheOptions { WarmQueueCapacity = 10 } }), _metrics);
        WarmRequest req1 = MakeRequest("0");
        WarmRequest req2 = MakeRequest("1");

        Assert.True(queue.TryEnqueue(req1));
        Assert.True(queue.TryEnqueue(req2));
    }

    [Fact]
    public void TryEnqueue_WhenChannelFull_ReturnsFalseAndClearsGuard()
    {
        // Create a queue with capacity 1 so the second distinct enqueue fills it.
        var queue = new CacheWarmingQueue(Options.Create(new MirrorOptions { Cache = new CacheOptions { WarmQueueCapacity = 1 } }), _metrics);

        string hexA = new('a', 64);
        string hexB = new('b', 64);
        var reqA = new WarmRequest("library/nginx", "blobs", new Digest("sha256", hexA));
        var reqB = new WarmRequest("library/nginx", "blobs", new Digest("sha256", hexB));

        // First item fills the channel.
        Assert.True(queue.TryEnqueue(reqA));

        // Second item should be dropped (channel full) and the guard cleaned up.
        Assert.False(queue.TryEnqueue(reqB));

        // Because the guard was cleaned up, B can be enqueued once the channel drains.
        queue.Reader.TryRead(out _); // drain A
        Assert.True(queue.TryEnqueue(reqB));
    }

    [Fact]
    public void TryEnqueue_ChannelFull_DoesNotLeakGuardEntry()
    {
        var queue = new CacheWarmingQueue(Options.Create(new MirrorOptions { Cache = new CacheOptions { WarmQueueCapacity = 1 } }), _metrics);
        string hexFill = new('f', 64);
        string hexOverflow = new('e', 64);
        var reqFill = new WarmRequest("library/nginx", "blobs", new Digest("sha256", hexFill));
        var reqOverflow = new WarmRequest("library/nginx", "blobs", new Digest("sha256", hexOverflow));

        queue.TryEnqueue(reqFill);           // fills the channel
        queue.TryEnqueue(reqOverflow);       // dropped — guard must be released

        // The overflow digest must not be stuck; re-enqueue after drain succeeds.
        queue.Reader.TryRead(out _);         // drain the channel
        Assert.True(queue.TryEnqueue(reqOverflow));
    }
}
