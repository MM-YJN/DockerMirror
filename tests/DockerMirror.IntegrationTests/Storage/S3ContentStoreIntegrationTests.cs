using System.Security.Cryptography;

using DockerMirror.Caching;
using DockerMirror.Caching.S3;
using DockerMirror.Configuration;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DockerMirror.IntegrationTests.Storage;

public sealed class S3ContentStoreIntegrationTests(TestInfrastructure.MinioFixture fixture) : IClassFixture<TestInfrastructure.MinioFixture>
{
    private S3ContentStore CreateStore(string keyPrefix = "")
    {
        var s3Options = new S3CacheOptions
        {
            Bucket = TestInfrastructure.MinioFixture.BucketName,
            Region = "us-east-1",
            ServiceUrl = fixture.Endpoint,
            AccessKey = TestInfrastructure.MinioFixture.AccessKey,
            SecretKey = TestInfrastructure.MinioFixture.SecretKey,
            UsePathStyle = true,
            KeyPrefix = keyPrefix,
        };

        var cacheOptions = new MirrorOptions { Cache = new CacheOptions { Enabled = true, S3 = s3Options } };
        IOptions<MirrorOptions> options = Options.Create(cacheOptions);
        S3Client client = fixture.CreateS3Client();
        return new S3ContentStore(client, options, NullLogger<S3ContentStore>.Instance);
    }

    [Fact(Timeout = 120_000)]
    [Trait("Category", "Integration")]
    public async Task WriteAndRead_Success()
    {
        if (!fixture.DockerAvailable)
        {
            Assert.Skip("Docker is not available");
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        S3ContentStore store = CreateStore();
        byte[] content = "fake-blob-content"u8.ToArray();
        Digest expectedDigest = ComputeSha256(content);
        string key = DigestCacheKey.FromDigest(expectedDigest);

        await using (ICacheWriteHandle handle = await store.BeginWriteAsync(key, expectedDigest, ct))
        {
            handle.SetMetadata(new CacheEntryMetadata("application/octet-stream"));
            await handle.Stream.WriteAsync(content, ct);
            bool committed = await handle.CommitAsync(ct);
            Assert.True(committed);
        }

        CachedContent? hit = await store.TryGetAsync(key, ct);
        Assert.NotNull(hit);
        Assert.Equal(content.Length, hit.Length);
        Assert.Equal("application/octet-stream", hit.ContentType);
        Assert.Equal(expectedDigest.Canonical, hit.Digest);
        Assert.NotNull(hit.StoredAtUtc);

        byte[] buffer = new byte[content.Length];
        await hit.Stream.ReadExactlyAsync(buffer, ct);
        Assert.Equal(content, buffer);

        await hit.Stream.DisposeAsync();
    }

    [Fact(Timeout = 120_000)]
    [Trait("Category", "Integration")]
    public async Task TryGetAsync_ReturnsNull_WhenKeyNotFound()
    {
        if (!fixture.DockerAvailable)
        {
            Assert.Skip("Docker is not available");
        }

        S3ContentStore store = CreateStore();

        CachedContent? result = await store.TryGetAsync(
            "nonexistent/key",
            TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact(Timeout = 120_000)]
    [Trait("Category", "Integration")]
    public async Task AbortedWrite_DoesNotPersistObject()
    {
        if (!fixture.DockerAvailable)
        {
            Assert.Skip("Docker is not available");
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        S3ContentStore store = CreateStore();
        var expectedDigest = Digest.Parse("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        string key = DigestCacheKey.FromDigest(expectedDigest);

        ICacheWriteHandle handle = await store.BeginWriteAsync(key, expectedDigest, ct);
        handle.SetMetadata(new CacheEntryMetadata("application/octet-stream"));
        await handle.Stream.WriteAsync("incomplete"u8.ToArray(), ct);
        await handle.DisposeAsync();

        CachedContent? hit = await store.TryGetAsync(key, ct);
        Assert.Null(hit);
    }

    [Fact(Timeout = 120_000)]
    [Trait("Category", "Integration")]
    public async Task DigestMismatch_ReturnsFalse_AndDoesNotPersist()
    {
        if (!fixture.DockerAvailable)
        {
            Assert.Skip("Docker is not available");
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        S3ContentStore store = CreateStore();
        byte[] content = "real-content"u8.ToArray();
        var wrongDigest = Digest.Parse("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        string key = DigestCacheKey.FromDigest(wrongDigest);

        await using (ICacheWriteHandle handle = await store.BeginWriteAsync(key, wrongDigest, ct))
        {
            handle.SetMetadata(new CacheEntryMetadata("application/octet-stream"));
            await handle.Stream.WriteAsync(content, ct);
            bool committed = await handle.CommitAsync(ct);
            Assert.False(committed);
        }

        CachedContent? hit = await store.TryGetAsync(key, ct);
        Assert.Null(hit);
    }

    [Fact(Timeout = 120_000)]
    [Trait("Category", "Integration")]
    public async Task ConsecutiveCommits_OverwriteExistingObject()
    {
        if (!fixture.DockerAvailable)
        {
            Assert.Skip("Docker is not available");
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        S3ContentStore store = CreateStore();
        byte[] content1 = "first-version"u8.ToArray();
        byte[] content2 = "second-version-with-more-data"u8.ToArray();
        Digest digest1 = ComputeSha256(content1);
        Digest digest2 = ComputeSha256(content2);

        // Overwriting the same S3 key with content whose digest differs from
        // the key is a test-only scenario; in production, keys always match their digest.
        string key = DigestCacheKey.FromDigest(digest1);

        await using (ICacheWriteHandle handle1 = await store.BeginWriteAsync(key, digest1, ct))
        {
            handle1.SetMetadata(new CacheEntryMetadata("application/octet-stream"));
            await handle1.Stream.WriteAsync(content1, ct);
            await handle1.CommitAsync(ct);
        }

        await using (ICacheWriteHandle handle2 = await store.BeginWriteAsync(key, digest2, ct))
        {
            handle2.SetMetadata(new CacheEntryMetadata("application/octet-stream"));
            await handle2.Stream.WriteAsync(content2, ct);
            await handle2.CommitAsync(ct);
        }

        CachedContent? hit = await store.TryGetAsync(key, ct);
        Assert.NotNull(hit);
        Assert.Equal(content2.Length, hit.Length);
        Assert.Equal(digest2.Canonical, hit.Digest);

        byte[] buffer = new byte[content2.Length];
        await hit.Stream.ReadExactlyAsync(buffer, ct);
        Assert.Equal(content2, buffer);

        await hit.Stream.DisposeAsync();
    }

    [Fact(Timeout = 120_000)]
    [Trait("Category", "Integration")]
    public async Task WriteAndRead_CustomContentType()
    {
        if (!fixture.DockerAvailable)
        {
            Assert.Skip("Docker is not available");
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        S3ContentStore store = CreateStore();
        byte[] content = "manifest-json"u8.ToArray();
        Digest expectedDigest = ComputeSha256(content);
        string key = DigestCacheKey.FromDigest(expectedDigest);

        await using (ICacheWriteHandle handle = await store.BeginWriteAsync(key, expectedDigest, ct))
        {
            handle.SetMetadata(new CacheEntryMetadata("application/vnd.docker.distribution.manifest.v2+json"));
            await handle.Stream.WriteAsync(content, ct);
            await handle.CommitAsync(ct);
        }

        CachedContent? hit = await store.TryGetAsync(key, ct);
        Assert.NotNull(hit);
        Assert.Equal("application/vnd.docker.distribution.manifest.v2+json", hit.ContentType);
        await hit.Stream.DisposeAsync();
    }

    [Fact(Timeout = 120_000)]
    [Trait("Category", "Integration")]
    public async Task WriteAndRead_WithKeyPrefix()
    {
        if (!fixture.DockerAvailable)
        {
            Assert.Skip("Docker is not available");
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        S3ContentStore store = CreateStore("docker-cache/");
        byte[] content = "prefix-test"u8.ToArray();
        Digest expectedDigest = ComputeSha256(content);
        string key = DigestCacheKey.FromDigest(expectedDigest);

        await using (ICacheWriteHandle handle = await store.BeginWriteAsync(key, expectedDigest, ct))
        {
            handle.SetMetadata(new CacheEntryMetadata("application/octet-stream"));
            await handle.Stream.WriteAsync(content, ct);
            await handle.CommitAsync(ct);
        }

        CachedContent? hit = await store.TryGetAsync(key, ct);
        Assert.NotNull(hit);
        Assert.Equal(content.Length, hit.Length);

        byte[] buffer = new byte[content.Length];
        await hit.Stream.ReadExactlyAsync(buffer, ct);
        Assert.Equal(content, buffer);

        await hit.Stream.DisposeAsync();
    }

    [Fact(Timeout = 120_000)]
    [Trait("Category", "Integration")]
    public async Task EnumerateAsync_AndDeleteAsync()
    {
        if (!fixture.DockerAvailable)
        {
            Assert.Skip("Docker is not available");
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        S3ContentStore store = CreateStore("evict-test/");

        byte[] content = "entry-content"u8.ToArray();
        Digest expectedDigest = ComputeSha256(content);
        string key = DigestCacheKey.FromDigest(expectedDigest);

        await using (ICacheWriteHandle handle = await store.BeginWriteAsync(key, expectedDigest, ct))
        {
            handle.SetMetadata(new CacheEntryMetadata("application/octet-stream"));
            await handle.Stream.WriteAsync(content, ct);
            await handle.CommitAsync(ct);
        }

        var entries = new List<CacheEntryInfo>();
        await foreach (CacheEntryInfo entry in store.EnumerateAsync(ct))
        {
            entries.Add(entry);
        }

        Assert.Contains(entries, e => e.Key == key);

        await store.DeleteAsync(key, ct);

        CachedContent? hit = await store.TryGetAsync(key, ct);
        Assert.Null(hit);
    }

    [Fact(Timeout = 120_000)]
    [Trait("Category", "Integration")]
    public async Task TouchAsync_PreservesDigestAndContentType()
    {
        if (!fixture.DockerAvailable)
        {
            Assert.Skip("Docker is not available");
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        S3ContentStore store = CreateStore("touch-test/");

        byte[] content = "touch-content"u8.ToArray();
        Digest expectedDigest = ComputeSha256(content);
        string key = DigestCacheKey.FromDigest(expectedDigest);
        const string ContentType = "application/vnd.docker.distribution.manifest.v2+json";

        await using (ICacheWriteHandle handle = await store.BeginWriteAsync(key, expectedDigest, ct))
        {
            handle.SetMetadata(new CacheEntryMetadata(ContentType));
            await handle.Stream.WriteAsync(content, ct);
            await handle.CommitAsync(ct);
        }

        // Touch is used by LRU eviction; it must preserve all metadata so subsequent
        // cache hits still return the correct Docker-Content-Digest and Content-Type.
        await store.TouchAsync(key, ct);

        CachedContent? hit = await store.TryGetAsync(key, ct);
        Assert.NotNull(hit);
        Assert.Equal(expectedDigest.Canonical, hit.Digest);
        Assert.Equal(ContentType, hit.ContentType);
        Assert.NotNull(hit.StoredAtUtc);
        await hit.Stream.DisposeAsync();
    }

    [Fact(Timeout = 120_000)]
    [Trait("Category", "Integration")]
    public async Task TouchAsync_PreservesFetchedAt()
    {
        if (!fixture.DockerAvailable)
        {
            Assert.Skip("Docker is not available");
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        S3ContentStore store = CreateStore("touch-fetched-at/");

        byte[] content = "touch-fetched-at-content"u8.ToArray();
        Digest expectedDigest = ComputeSha256(content);
        string key = DigestCacheKey.FromDigest(expectedDigest);
        const string ContentType = "application/vnd.docker.distribution.manifest.v2+json";

        await using (ICacheWriteHandle handle = await store.BeginWriteAsync(key, expectedDigest, ct))
        {
            handle.SetMetadata(new CacheEntryMetadata(ContentType));
            await handle.Stream.WriteAsync(content, ct);
            await handle.CommitAsync(ct);
        }

        // Read StoredAtUtc (fetched-at from x-amz-meta-fetched-at) before touch.
        CachedContent? before = await store.TryGetAsync(key, ct);
        Assert.NotNull(before);
        DateTimeOffset? fetchedAtBefore = before.StoredAtUtc;
        Assert.NotNull(fetchedAtBefore);
        await before.Stream.DisposeAsync();

        // Touch advances LastModified but must not overwrite x-amz-meta-fetched-at.
        await store.TouchAsync(key, ct);

        CachedContent? after = await store.TryGetAsync(key, ct);
        Assert.NotNull(after);
        Assert.Equal(expectedDigest.Canonical, after.Digest);
        Assert.Equal(ContentType, after.ContentType);
        Assert.NotNull(after.StoredAtUtc);
        // x-amz-meta-fetched-at must be preserved across touch.
        Assert.Equal(fetchedAtBefore.Value, after.StoredAtUtc.Value);
        await after.Stream.DisposeAsync();
    }

    [Fact(Timeout = 120_000)]
    [Trait("Category", "Integration")]
    public async Task TagPointer_SetAndGet_RoundTrip()
    {
        if (!fixture.DockerAvailable)
        {
            Assert.Skip("Docker is not available");
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        S3ContentStore store = CreateStore("tag-ptr-test/");

        Digest digest = ComputeSha256("tag-pointer-manifest"u8.ToArray());
        var resolvedAt = DateTimeOffset.FromUnixTimeMilliseconds(
            DateTimeOffset.UtcNow.AddMinutes(-3).ToUnixTimeMilliseconds());
        var pointer = new TagPointer(digest, resolvedAt);
        string key = TagCacheKey.From("library/nginx", "latest", "application/vnd.docker.distribution.manifest.v2+json");

        await store.SetTagAsync(key, pointer, ct);

        TagPointer? retrieved = await store.TryGetTagAsync(key, ct);
        Assert.NotNull(retrieved);
        Assert.Equal(digest, retrieved.Value.Digest);
        Assert.Equal(resolvedAt, retrieved.Value.ResolvedAtUtc);
    }

    [Fact(Timeout = 120_000)]
    [Trait("Category", "Integration")]
    public async Task TagPointer_TryGet_ReturnsNull_WhenNotFound()
    {
        if (!fixture.DockerAvailable)
        {
            Assert.Skip("Docker is not available");
        }

        S3ContentStore store = CreateStore("tag-ptr-test/");

        TagPointer? result = await store.TryGetTagAsync(
            "tags/xx/yy/nonexistent",
            TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact(Timeout = 120_000)]
    [Trait("Category", "Integration")]
    public async Task TagPointer_Overwrite_UpdatesValue()
    {
        if (!fixture.DockerAvailable)
        {
            Assert.Skip("Docker is not available");
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        S3ContentStore store = CreateStore("tag-ptr-test/");

        Digest digest1 = ComputeSha256("v1-manifest"u8.ToArray());
        Digest digest2 = ComputeSha256("v2-manifest"u8.ToArray());
        var t1 = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        string key = TagCacheKey.From("library/alpine", "edge", null);

        await store.SetTagAsync(key, new TagPointer(digest1, t1), ct);
        await store.SetTagAsync(key, new TagPointer(digest2, t2), ct);

        TagPointer? retrieved = await store.TryGetTagAsync(key, ct);
        Assert.NotNull(retrieved);
        Assert.Equal(digest2, retrieved.Value.Digest);
        Assert.Equal(t2, retrieved.Value.ResolvedAtUtc);
    }

    private static Digest ComputeSha256(byte[] content)
    {
        byte[] hash = SHA256.HashData(content);
        return new Digest("sha256", Convert.ToHexStringLower(hash));
    }

    [Fact(Timeout = 120_000)]
    [Trait("Category", "Integration")]
    public async Task TryGetRangeAsync_PrefixRange_ReturnsPartialContent()
    {
        if (!fixture.DockerAvailable)
        {
            Assert.Skip("Docker is not available");
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        S3ContentStore store = CreateStore();
        byte[] content = "hello-range-test-content"u8.ToArray();
        Digest expectedDigest = ComputeSha256(content);
        string key = DigestCacheKey.FromDigest(expectedDigest);

        await using (ICacheWriteHandle handle = await store.BeginWriteAsync(key, expectedDigest, ct))
        {
            handle.SetMetadata(new CacheEntryMetadata("application/octet-stream"));
            await handle.Stream.WriteAsync(content, ct);
            bool committed = await handle.CommitAsync(ct);
            Assert.True(committed);
        }

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

    [Fact(Timeout = 120_000)]
    [Trait("Category", "Integration")]
    public async Task TryGetRangeAsync_SuffixRange_ReturnsTailContent()
    {
        if (!fixture.DockerAvailable)
        {
            Assert.Skip("Docker is not available");
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        S3ContentStore store = CreateStore();
        byte[] content = "suffix-range-test"u8.ToArray();
        Digest expectedDigest = ComputeSha256(content);
        string key = DigestCacheKey.FromDigest(expectedDigest);

        await using (ICacheWriteHandle handle = await store.BeginWriteAsync(key, expectedDigest, ct))
        {
            handle.SetMetadata(new CacheEntryMetadata("application/octet-stream"));
            await handle.Stream.WriteAsync(content, ct);
            bool committed = await handle.CommitAsync(ct);
            Assert.True(committed);
        }

        RangedContent? ranged = await store.TryGetRangeAsync(key, "bytes=-4", ct);
        Assert.NotNull(ranged);
        await using (ranged)
        {
            Assert.Equal(206, ranged.StatusCode);
            Assert.Equal(4, ranged.ContentLength);

            byte[] buffer = new byte[4];
            await ranged.Stream.ReadExactlyAsync(buffer, ct);
            Assert.Equal(content[^4..], buffer);
        }
    }

    [Fact(Timeout = 120_000)]
    [Trait("Category", "Integration")]
    public async Task TryGetRangeAsync_UnsatisfiableRange_Returns416()
    {
        if (!fixture.DockerAvailable)
        {
            Assert.Skip("Docker is not available");
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        S3ContentStore store = CreateStore();
        byte[] content = "unsatisfiable-range"u8.ToArray();
        Digest expectedDigest = ComputeSha256(content);
        string key = DigestCacheKey.FromDigest(expectedDigest);

        await using (ICacheWriteHandle handle = await store.BeginWriteAsync(key, expectedDigest, ct))
        {
            handle.SetMetadata(new CacheEntryMetadata("application/octet-stream"));
            await handle.Stream.WriteAsync(content, ct);
            bool committed = await handle.CommitAsync(ct);
            Assert.True(committed);
        }

        RangedContent? ranged = await store.TryGetRangeAsync(key, $"bytes={content.Length + 10}-", ct);
        Assert.NotNull(ranged);
        await using (ranged)
        {
            Assert.Equal(416, ranged.StatusCode);
            Assert.Equal(0, ranged.ContentLength);
            Assert.Equal($"bytes */{content.Length}", ranged.ContentRange);
        }
    }
}
