using System.Net;

using DockerMirror.Caching;
using DockerMirror.Caching.S3;
using DockerMirror.Configuration;
using DockerMirror.UnitTests.TestInfrastructure;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DockerMirror.UnitTests.Caching;

public sealed class S3CachePreflightServiceTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static S3Client CreateClient(IOptions<MirrorOptions> options, Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var httpClient = new HttpClient(new TestHttpMessageHandler(handler));
        return new S3Client(httpClient, options, NullLogger<S3Client>.Instance);
    }

    private static S3CachePreflightService CreateService(IOptions<MirrorOptions> options, S3Client client)
        => new(options, client, NullLogger<S3CachePreflightService>.Instance);

    private static IOptions<MirrorOptions> EnabledOptions(string bucket = "my-bucket")
        => Options.Create(new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                S3 = new S3CacheOptions
                {
                    Bucket = bucket,
                    Region = "us-east-1",
                    AccessKey = "key",
                    SecretKey = "secret",
                    UsePathStyle = true,
                    ServiceUrl = "http://localhost:9000",
                },
            },
        });

    // -----------------------------------------------------------------------
    // Tests
    // -----------------------------------------------------------------------

    [Fact]
    public async Task StartingAsync_NoOp_WhenCacheDisabled()
    {
        IOptions<MirrorOptions> options = Options.Create(new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = false,
                S3 = new S3CacheOptions { Bucket = "my-bucket" },
            },
        });

        bool called = false;
        S3Client client = CreateClient(options, _ =>
        {
            called = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        S3CachePreflightService service = CreateService(options, client);
        await service.StartingAsync(CancellationToken.None);

        Assert.False(called, "S3 client should not be called when caching is disabled.");
    }

    [Fact]
    public async Task StartingAsync_Throws_WhenBucketIsEmpty()
    {
        IOptions<MirrorOptions> options = Options.Create(new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                S3 = new S3CacheOptions { Bucket = "" },
            },
        });

        S3Client client = CreateClient(options, _ => throw new InvalidOperationException("should not be called"));
        S3CachePreflightService service = CreateService(options, client);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartingAsync(CancellationToken.None));

        Assert.Contains("Bucket", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartingAsync_Throws_WhenBucketUnreachable()
    {
        IOptions<MirrorOptions> options = EnabledOptions("unreachable-bucket");
        S3Client client = CreateClient(options, _ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        S3CachePreflightService service = CreateService(options, client);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartingAsync(CancellationToken.None));

        Assert.Contains("unreachable-bucket", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("S3 bucket", ex.Message, StringComparison.OrdinalIgnoreCase);
        // Inner exception should carry the original S3 error.
        Assert.IsType<S3Exception>(ex.InnerException);
    }

    [Fact]
    public async Task StartingAsync_Succeeds_WhenBucketReachable()
    {
        IOptions<MirrorOptions> options = EnabledOptions("reachable-bucket");
        S3Client client = CreateClient(options, _ => new HttpResponseMessage(HttpStatusCode.OK));
        S3CachePreflightService service = CreateService(options, client);

        // Should complete without throwing.
        await service.StartingAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartingAsync_Throws_WhenHttpRequestFails()
    {
        IOptions<MirrorOptions> options = EnabledOptions("error-bucket");
        S3Client client = CreateClient(options, _ => throw new HttpRequestException("connection refused"));
        S3CachePreflightService service = CreateService(options, client);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartingAsync(CancellationToken.None));

        Assert.Contains("error-bucket", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }
}
