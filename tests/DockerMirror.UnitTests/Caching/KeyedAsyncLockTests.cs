using DockerMirror.Caching;
using DockerMirror.Diagnostics;

using Microsoft.Extensions.Logging.Abstractions;

namespace DockerMirror.UnitTests.Caching;

public sealed class KeyedAsyncLockTests : IDisposable
{
    private readonly MirrorMetrics _metrics = new();

    public void Dispose() => _metrics.Dispose();
    [Fact]
    public async Task LockAsync_SerializesAccess_SameKey()
    {
        var locker = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance, _metrics);
        bool completed = false;
        CancellationToken ct = TestContext.Current.CancellationToken;

        using (await locker.LockAsync("key-a", ct))
        {
            var task = Task.Run(async () =>
            {
                using (await locker.LockAsync("key-a", ct))
                {
                    completed = true;
                }
            }, ct);

            await Task.Delay(50, ct);
            Assert.False(completed);
        }

        await Task.Delay(100, ct);
        Assert.True(completed);
    }

    [Fact]
    public async Task LockAsync_AllowsConcurrentAccess_DifferentKeys()
    {
        var locker = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance, _metrics);
        CancellationToken ct = TestContext.Current.CancellationToken;
        bool completed = false;

        using (await locker.LockAsync("key-a", ct))
        {
            _ = Task.Run(async () =>
            {
                using (await locker.LockAsync("key-b", ct))
                {
                    completed = true;
                }
            }, ct);

            await Task.Delay(100, ct);
            Assert.True(completed);
        }
    }

    [Fact]
    public async Task LockAsync_ReleasesOnDispose()
    {
        var locker = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance, _metrics);
        CancellationToken ct = TestContext.Current.CancellationToken;

        using (await locker.LockAsync("key", ct))
        {
        }

        bool entered = false;
        using (await locker.LockAsync("key", ct))
        {
            entered = true;
        }

        Assert.True(entered, "Second LockAsync call should complete after the first releaser is disposed.");
    }

    [Fact]
    public async Task LockAsync_HighConcurrency_SameKey_SerializesAccess()
    {
        var locker = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance, _metrics);
        CancellationToken ct = TestContext.Current.CancellationToken;
        int concurrency = 0;
        int maxConcurrency = 0;
        int completed = 0;

        async Task Worker()
        {
            using (await locker.LockAsync("shared-key", ct))
            {
                int current = Interlocked.Increment(ref concurrency);
                InterlockedMax(ref maxConcurrency, current);
                await Task.Yield();
                Interlocked.Decrement(ref concurrency);
            }

            Interlocked.Increment(ref completed);
        }

        Task[] tasks = Enumerable.Range(0, 100).Select(_ => Worker()).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(100, completed);
        Assert.Equal(1, maxConcurrency);
    }

    [Fact]
    public async Task LockAsync_HighConcurrency_DifferentKeys_AllowsParallelism()
    {
        var locker = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance, _metrics);
        CancellationToken ct = TestContext.Current.CancellationToken;
        int concurrency = 0;
        int maxConcurrency = 0;
        int completed = 0;

        async Task Worker(string key)
        {
            using (await locker.LockAsync(key, ct))
            {
                int current = Interlocked.Increment(ref concurrency);
                InterlockedMax(ref maxConcurrency, current);
                await Task.Yield();
                Interlocked.Decrement(ref concurrency);
            }

            Interlocked.Increment(ref completed);
        }

        Task[] tasks = Enumerable.Range(0, 100).Select(i => Worker($"key-{i}")).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(100, completed);
        Assert.True(maxConcurrency > 1, $"Expected parallelism > 1, got {maxConcurrency}");
    }

    [Fact]
    public async Task LockAsync_CleansUpSemaphore_AfterAllReleasersDisposed()
    {
        var locker = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance, _metrics);
        CancellationToken ct = TestContext.Current.CancellationToken;

        Assert.Equal(0, locker.Count);

        using (await locker.LockAsync("key", ct))
        {
            Assert.Equal(1, locker.Count);
        }

        Assert.Equal(0, locker.Count);
    }

    [Fact]
    public async Task LockAsync_CleansUpSemaphore_AfterConcurrentAccess()
    {
        var locker = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance, _metrics);
        CancellationToken ct = TestContext.Current.CancellationToken;

        async Task Worker(string key)
        {
            using (await locker.LockAsync(key, ct))
            {
                await Task.Yield();
            }
        }

        Task[] tasks = Enumerable.Range(0, 100).Select(i => Worker($"key-{i}")).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(0, locker.Count);
    }

    [Fact]
    public async Task LockAsync_ReusesEntry_UnderContention()
    {
        var locker = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance, _metrics);
        CancellationToken ct = TestContext.Current.CancellationToken;
        var firstEntered = new TaskCompletionSource();
        var secondCompleted = new TaskCompletionSource();

        var task2 = Task.Run(async () =>
        {
            using (await locker.LockAsync("key", ct))
            {
                secondCompleted.SetResult();
            }
        }, ct);

        var task1 = Task.Run(async () =>
        {
            using (await locker.LockAsync("key", ct))
            {
                Assert.Equal(1, locker.Count);
                firstEntered.SetResult();
                await Task.Delay(100, ct);
            }
        }, ct);

        await firstEntered.Task;
        await secondCompleted.Task;
        await Task.WhenAll(task1, task2);
    }

    [Fact]
    public async Task LockAsync_Cancellation_DoesNotOverReleaseSemaphore()
    {
        var locker = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance, _metrics);
        CancellationToken testCt = TestContext.Current.CancellationToken;
        using var cts = new CancellationTokenSource();

        var holderEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holderRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var holderTask = Task.Run(async () =>
        {
            using (await locker.LockAsync("key", testCt))
            {
                holderEntered.SetResult();
                await holderRelease.Task.WaitAsync(testCt).ConfigureAwait(false);
            }
        }, testCt);

        await holderEntered.Task;

        var waiterTask = Task.Run(async () =>
        {
            await locker.LockAsync("key", cts.Token);
        }, testCt);

        await Task.Delay(50, testCt);
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => waiterTask);

        Assert.Equal(1, locker.Count);

        holderRelease.SetResult();
        await holderTask;
        Assert.Equal(0, locker.Count);
    }

    [Fact]
    public async Task LockAsync_Cancellation_CleansUpEntry_WhenNoOtherWaiters()
    {
        var locker = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance, _metrics);
        CancellationToken testCt = TestContext.Current.CancellationToken;
        using var cts = new CancellationTokenSource();

        var holderEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holderRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var holderTask = Task.Run(async () =>
        {
            using (await locker.LockAsync("key", testCt))
            {
                holderEntered.SetResult();
                await holderRelease.Task.WaitAsync(testCt).ConfigureAwait(false);
            }
        }, testCt);

        await holderEntered.Task;

        var waiterTask = Task.Run(async () =>
        {
            await locker.LockAsync("key", cts.Token);
        }, testCt);

        await Task.Delay(50, testCt);
        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => waiterTask);

        holderRelease.SetResult();
        await holderTask;
        Assert.Equal(0, locker.Count);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int snapshot;
        do
        {
            snapshot = Volatile.Read(ref target);
        }
        while (value > snapshot && Interlocked.CompareExchange(ref target, value, snapshot) != snapshot);
    }
}
