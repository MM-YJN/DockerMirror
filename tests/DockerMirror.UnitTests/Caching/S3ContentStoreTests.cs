using DockerMirror.Caching;
using DockerMirror.Caching.S3;
using DockerMirror.Configuration;

using Microsoft.Extensions.Options;

namespace DockerMirror.UnitTests.Caching;

public sealed class S3ContentStoreTests
{
    [Fact]
    public async Task BuildS3Key_RejectsPathTraversal()
    {
        S3ContentStore store = CreateBrokenStore();

        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            store.BeginWriteAsync(
                "../etc/passwd",
                new Digest("sha256", new string('a', 64)),
                TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("Path traversal", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildS3Key_RejectsDotDotInMiddle()
    {
        S3ContentStore store = CreateBrokenStore();

        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            store.BeginWriteAsync(
                "sha256/../ab/abcdef",
                new Digest("sha256", new string('a', 64)),
                TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("Path traversal", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static S3ContentStore CreateBrokenStore()
    {
        var cacheOptions = new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                S3 = new S3CacheOptions { Bucket = "test", Region = "us-east-1" },
            },
        };
        IOptions<MirrorOptions> options = Options.Create(cacheOptions);

        var httpClient = new HttpClient(new SocketsHttpHandler());
        var client = new S3Client(httpClient, options, Microsoft.Extensions.Logging.Abstractions.NullLogger<S3Client>.Instance);
        return new S3ContentStore(
            client,
            options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<S3ContentStore>.Instance);
    }
}
