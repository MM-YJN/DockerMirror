using System.Security.Cryptography;

using DockerMirror.Caching;
using DockerMirror.Configuration;

using Microsoft.Extensions.Options;

namespace DockerMirror.UnitTests.Caching;

public sealed class FileSystemContentStoreTests : IAsyncDisposable
{
    private readonly string _cacheDir;

    public FileSystemContentStoreTests()
        => _cacheDir = Path.Join(Path.GetTempPath(), "docker-mirror-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TryGetAsync_ReturnsNull_WhenFileDoesNotExist()
    {
        FileSystemContentStore store = CreateStore();

        CachedContent? result = await store.TryGetAsync("sha256/ab/cd/abcdef01", TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task WriteAndRead_Success()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FileSystemContentStore store = CreateStore();
        byte[] content = "fake-blob-content"u8.ToArray();
        Digest expectedDigest = ComputeSha256(content);

        await WriteAndCommit(store, content, expectedDigest, "application/octet-stream", ct);

        string key = DigestCacheKey.FromDigest(expectedDigest);
        CachedContent? hit = await store.TryGetAsync(key, ct);
        Assert.NotNull(hit);
        Assert.Equal(content.Length, hit.Length);
        Assert.Equal("application/octet-stream", hit.ContentType);
        Assert.Equal(expectedDigest.Canonical, hit.Digest);
        Assert.NotNull(hit.StoredAtUtc);

        byte[] buffer = new byte[content.Length];
        int read = await hit.Stream.ReadAsync(buffer, ct);
        Assert.Equal(content.Length, read);
        Assert.Equal(content, buffer);

        await hit.Stream.DisposeAsync();
    }

    [Fact]
    public async Task AbortedWrite_DoesNotLeaveFile()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FileSystemContentStore store = CreateStore();
        var expectedDigest = Digest.Parse("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        string key = DigestCacheKey.FromDigest(expectedDigest);
        ICacheWriteHandle handle = await store.BeginWriteAsync(key, expectedDigest, ct);
        handle.SetMetadata(new CacheEntryMetadata("application/octet-stream"));
        await handle.Stream.WriteAsync("incomplete"u8.ToArray(), ct);
        await handle.DisposeAsync();

        CachedContent? hit = await store.TryGetAsync(key, ct);
        Assert.Null(hit);
    }

    [Fact]
    public async Task DigestMismatch_OnCommit_DoesNotCache()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FileSystemContentStore store = CreateStore();
        byte[] content = "real-content"u8.ToArray();
        // Use a WRONG expected digest
        var wrongDigest = Digest.Parse("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        string key = DigestCacheKey.FromDigest(wrongDigest);
        await using (ICacheWriteHandle handle = await store.BeginWriteAsync(key, wrongDigest, ct))
        {
            handle.SetMetadata(new CacheEntryMetadata("application/octet-stream"));
            await handle.Stream.WriteAsync(content, ct);
            await handle.CommitAsync(ct);
        }

        CachedContent? hit = await store.TryGetAsync(key, ct);
        Assert.Null(hit);
    }

    [Fact]
    public async Task MetaFile_StoresMetadata()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FileSystemContentStore store = CreateStore();
        byte[] content = "hello-world"u8.ToArray();
        Digest expectedDigest = ComputeSha256(content);

        await WriteAndCommit(store, content, expectedDigest, "application/vnd.docker.distribution.manifest.v2+json", ct);

        string key = DigestCacheKey.FromDigest(expectedDigest);
        string contentPath = Path.Join(_cacheDir, key.Replace('/', Path.DirectorySeparatorChar));
        string metaPath = contentPath + ".meta";
        Assert.True(File.Exists(metaPath));

        string[] metaLines = await File.ReadAllLinesAsync(metaPath, ct);
        Assert.Contains("length=11", metaLines);
        Assert.Contains("content-type=application/vnd.docker.distribution.manifest.v2+json", metaLines);
        Assert.Contains(metaLines, line => line.StartsWith("fetched-at=", StringComparison.Ordinal));
        Assert.Contains($"digest={expectedDigest.Canonical}", metaLines);
    }

    [Fact]
    public async Task MetaFile_Absent_FallsBackToFileInfo()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FileSystemContentStore store = CreateStore();
        byte[] content = "fallback-test"u8.ToArray();
        Digest expectedDigest = ComputeSha256(content);

        await WriteAndCommit(store, content, expectedDigest, "application/octet-stream", ct);

        string key = DigestCacheKey.FromDigest(expectedDigest);
        string contentPath = Path.Join(_cacheDir, key.Replace('/', Path.DirectorySeparatorChar));

        // Delete the meta file
        string metaPath = contentPath + ".meta";
        File.Delete(metaPath);

        CachedContent? hit = await store.TryGetAsync(key, ct);
        Assert.NotNull(hit);
        Assert.Equal(content.Length, hit.Length);
        Assert.Equal("application/octet-stream", hit.ContentType);
        Assert.Empty(hit.Digest);
        await hit.Stream.DisposeAsync();
    }

    [Fact]
    public async Task MetaFile_Corrupt_FallsBackToFileInfo()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FileSystemContentStore store = CreateStore();
        byte[] content = "corrupt-meta"u8.ToArray();
        Digest expectedDigest = ComputeSha256(content);

        await WriteAndCommit(store, content, expectedDigest, "application/octet-stream", ct);

        string key = DigestCacheKey.FromDigest(expectedDigest);
        string contentPath = Path.Join(_cacheDir, key.Replace('/', Path.DirectorySeparatorChar));
        string metaPath = contentPath + ".meta";

        // Corrupt the meta file
        await File.WriteAllTextAsync(metaPath, "invalid\nlength=NaN", ct);

        CachedContent? hit = await store.TryGetAsync(key, ct);
        Assert.NotNull(hit);
        Assert.Equal(content.Length, hit.Length);
        await hit.Stream.DisposeAsync();
    }

    [Fact]
    public async Task Write_RecordsActualBytesWritten()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FileSystemContentStore store = CreateStore();
        byte[] content = "exact-bytes"u8.ToArray();
        Digest expectedDigest = ComputeSha256(content);

        // SetMetadata carries only ContentType; actual byte count is always used.
        string key = DigestCacheKey.FromDigest(expectedDigest);
        await using (ICacheWriteHandle handle = await store.BeginWriteAsync(key, expectedDigest, ct))
        {
            handle.SetMetadata(new CacheEntryMetadata("application/octet-stream"));
            await handle.Stream.WriteAsync(content, ct);
            await handle.CommitAsync(ct);
        }

        CachedContent? hit = await store.TryGetAsync(key, ct);
        Assert.NotNull(hit);
        Assert.Equal(11, hit.Length); // actual 11 bytes, not 999999
        await hit.Stream.DisposeAsync();
    }

    [Fact]
    public async Task KeyToPath_RejectsTraversal()
    {
        FileSystemContentStore store = CreateStore();

        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            store.BeginWriteAsync("../etc/passwd", new Digest("sha256", new string('a', 64)), TestContext.Current.CancellationToken).AsTask());
        Assert.Contains("path-traversal", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task KeyToPath_RejectsDotSegment()
    {
        FileSystemContentStore store = CreateStore();

        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            store.BeginWriteAsync("sha256/./ab/abcdef", new Digest("sha256", new string('a', 64)), TestContext.Current.CancellationToken).AsTask());
        Assert.Contains("path-traversal", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentWrites_ToSameKey_ResultInValidEntry()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FileSystemContentStore store = CreateStore();
        byte[] content = "concurrent"u8.ToArray();
        Digest expectedDigest = ComputeSha256(content);
        string key = DigestCacheKey.FromDigest(expectedDigest);

        IEnumerable<Task> tasks = Enumerable.Range(0, 3).Select(async _ =>
        {
            try
            {
                await using ICacheWriteHandle handle = await store.BeginWriteAsync(key, expectedDigest, ct);
                handle.SetMetadata(new CacheEntryMetadata("application/octet-stream"));
                await handle.Stream.WriteAsync(content, ct);
                await handle.CommitAsync(ct);
            }
            catch
            {
                // Concurrent writes may race; it's fine if one wins
            }
        });

        await Task.WhenAll(tasks);

        CachedContent? hit = await store.TryGetAsync(key, ct);
        Assert.NotNull(hit);
        Assert.Equal(content.Length, hit.Length);
        Assert.Equal(expectedDigest.Canonical, hit.Digest);
        await hit.Stream.DisposeAsync();
    }

    [Fact]
    public async Task TryGetRangeAsync_PrefixRange_ReturnsPartialContent()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FileSystemContentStore store = CreateStore();
        byte[] content = "hello-range-test-content"u8.ToArray();
        Digest expectedDigest = ComputeSha256(content);

        await WriteAndCommit(store, content, expectedDigest, "application/octet-stream", ct);

        string key = DigestCacheKey.FromDigest(expectedDigest);
        RangedContent? ranged = await store.TryGetRangeAsync(key, "bytes=0-4", ct);
        Assert.NotNull(ranged);
        await using (ranged)
        {
            Assert.Equal(206, ranged.StatusCode);
            Assert.Equal($"bytes 0-4/{content.Length}", ranged.ContentRange);
            Assert.Equal("application/octet-stream", ranged.ContentType);
            Assert.Equal(expectedDigest.Canonical, ranged.Digest);
            Assert.Equal(5, ranged.ContentLength);

            byte[] buffer = new byte[5];
            await ranged.Stream.ReadExactlyAsync(buffer, ct);
            Assert.Equal(content[..5], buffer);
        }
    }

    [Fact]
    public async Task TryGetRangeAsync_SuffixRange_ReturnsTailContent()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FileSystemContentStore store = CreateStore();
        byte[] content = "suffix-range-test"u8.ToArray();
        Digest expectedDigest = ComputeSha256(content);

        await WriteAndCommit(store, content, expectedDigest, "application/octet-stream", ct);

        string key = DigestCacheKey.FromDigest(expectedDigest);
        RangedContent? ranged = await store.TryGetRangeAsync(key, "bytes=-4", ct);
        Assert.NotNull(ranged);
        await using (ranged)
        {
            Assert.Equal(206, ranged.StatusCode);
            Assert.Equal(4, ranged.ContentLength);
            Assert.Equal($"bytes 13-16/{content.Length}", ranged.ContentRange);

            byte[] buffer = new byte[4];
            await ranged.Stream.ReadExactlyAsync(buffer, ct);
            Assert.Equal(content[^4..], buffer);
        }
    }

    [Fact]
    public async Task TryGetRangeAsync_OpenEndedRange_ReturnsThroughEnd()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FileSystemContentStore store = CreateStore();
        byte[] content = "open-ended-range-test"u8.ToArray();
        Digest expectedDigest = ComputeSha256(content);

        await WriteAndCommit(store, content, expectedDigest, "application/octet-stream", ct);

        string key = DigestCacheKey.FromDigest(expectedDigest);
        RangedContent? ranged = await store.TryGetRangeAsync(key, "bytes=5-", ct);
        Assert.NotNull(ranged);
        await using (ranged)
        {
            Assert.Equal(206, ranged.StatusCode);
            Assert.Equal(content.Length - 5, ranged.ContentLength);
            Assert.Equal($"bytes 5-{content.Length - 1}/{content.Length}", ranged.ContentRange);

            byte[] buffer = new byte[content.Length - 5];
            await ranged.Stream.ReadExactlyAsync(buffer, ct);
            Assert.Equal(content[5..], buffer);
        }
    }

    [Fact]
    public async Task TryGetRangeAsync_UnsatisfiableRange_Returns416()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FileSystemContentStore store = CreateStore();
        byte[] content = "unsatisfiable-range"u8.ToArray();
        Digest expectedDigest = ComputeSha256(content);

        await WriteAndCommit(store, content, expectedDigest, "application/octet-stream", ct);

        string key = DigestCacheKey.FromDigest(expectedDigest);
        RangedContent? ranged = await store.TryGetRangeAsync(key, $"bytes={content.Length + 10}-", ct);
        Assert.NotNull(ranged);
        await using (ranged)
        {
            Assert.Equal(416, ranged.StatusCode);
            Assert.Equal(0, ranged.ContentLength);
            Assert.Equal($"bytes */{content.Length}", ranged.ContentRange);
        }
    }

    [Fact]
    public async Task TryGetRangeAsync_MissingKey_ReturnsNull()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FileSystemContentStore store = CreateStore();

        RangedContent? ranged = await store.TryGetRangeAsync("sha256/ab/cd/abcdef01", "bytes=0-4", ct);
        Assert.Null(ranged);
    }

    [Fact]
    public async Task TryGetRangeAsync_MultipleRanges_ReturnsFullContent()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FileSystemContentStore store = CreateStore();
        byte[] content = "multi-range-test"u8.ToArray();
        Digest expectedDigest = ComputeSha256(content);

        await WriteAndCommit(store, content, expectedDigest, "application/octet-stream", ct);

        string key = DigestCacheKey.FromDigest(expectedDigest);
        RangedContent? ranged = await store.TryGetRangeAsync(key, "bytes=0-1,3-4", ct);
        Assert.NotNull(ranged);
        await using (ranged)
        {
            Assert.Equal(200, ranged.StatusCode);
            Assert.Equal(content.Length, ranged.ContentLength);
            Assert.Null(ranged.ContentRange);

            byte[] buffer = new byte[content.Length];
            await ranged.Stream.ReadExactlyAsync(buffer, ct);
            Assert.Equal(content, buffer);
        }
    }

    private static Digest ComputeSha256(byte[] content)
    {
        byte[] hash = SHA256.HashData(content);
        return new Digest("sha256", Convert.ToHexStringLower(hash));
    }

    private static async Task WriteAndCommit(
        IContentStore store, byte[] content, Digest expectedDigest, string contentType, CancellationToken ct)
    {
        string key = DigestCacheKey.FromDigest(expectedDigest);
        await using ICacheWriteHandle handle = await store.BeginWriteAsync(key, expectedDigest, ct);
        handle.SetMetadata(new CacheEntryMetadata(contentType));
        await handle.Stream.WriteAsync(content, ct);
        await handle.CommitAsync(ct);
    }

    private FileSystemContentStore CreateStore()
    {
        var cacheOptions = new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                FileSystem = new FileSystemCacheOptions { Directory = _cacheDir },
            },
        };
        IOptions<MirrorOptions> options = Options.Create(cacheOptions);
        return new FileSystemContentStore(options, Microsoft.Extensions.Logging.Abstractions.NullLogger<FileSystemContentStore>.Instance);
    }

    [Fact]
    public async Task TagPointer_SetAndGet_RoundTrip()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FileSystemContentStore store = CreateStore();
        string key = "tags/ab/cd/abcdef";
        var digest = Digest.Parse("sha256:" + new string('a', 64));
        var resolvedAt = new DateTimeOffset(2025, 1, 15, 10, 30, 0, TimeSpan.Zero);
        var pointer = new TagPointer(digest, resolvedAt);

        await store.SetTagAsync(key, pointer, ct);

        TagPointer? retrieved = await store.TryGetTagAsync(key, ct);
        Assert.NotNull(retrieved);
        Assert.Equal(digest, retrieved.Value.Digest);
        Assert.Equal(resolvedAt, retrieved.Value.ResolvedAtUtc);
    }

    [Fact]
    public async Task TagPointer_TryGet_ReturnsNull_WhenNotFound()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FileSystemContentStore store = CreateStore();

        TagPointer? result = await store.TryGetTagAsync("tags/xx/yy/nonexistent", ct);
        Assert.Null(result);
    }

    [Fact]
    public async Task TagPointer_Overwrite_UpdatesValue()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FileSystemContentStore store = CreateStore();
        string key = "tags/12/34/overwrite-test";

        var digest1 = Digest.Parse("sha256:" + new string('1', 64));
        var digest2 = Digest.Parse("sha256:" + new string('2', 64));
        var t1 = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        await store.SetTagAsync(key, new TagPointer(digest1, t1), ct);
        await store.SetTagAsync(key, new TagPointer(digest2, t2), ct);

        TagPointer? retrieved = await store.TryGetTagAsync(key, ct);
        Assert.NotNull(retrieved);
        Assert.Equal(digest2, retrieved.Value.Digest);
        Assert.Equal(t2, retrieved.Value.ResolvedAtUtc);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_cacheDir))
            {
                Directory.Delete(_cacheDir, recursive: true);
            }
        }
        catch
        {
            // ignore cleanup failures
        }

        await ValueTask.CompletedTask;
        GC.SuppressFinalize(this);
    }
}
