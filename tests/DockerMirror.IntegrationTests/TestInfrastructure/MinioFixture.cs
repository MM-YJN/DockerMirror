using DockerMirror.Caching.S3;
using DockerMirror.Configuration;
using DockerMirror.TestKit.Logger;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Xunit.Sdk;

namespace DockerMirror.IntegrationTests.TestInfrastructure;

public sealed class MinioFixture(IMessageSink messageSink) : IAsyncLifetime
{
    internal const string AccessKey = "testaccesskey";
    internal const string SecretKey = "testsecretkey";
    internal const string BucketName = "dockermirror";

    private IContainer? _container;

    public string Host { get; private set; } = "localhost";
    public int Port { get; private set; }

    public bool DockerAvailable { get; private set; }

    public string Endpoint => $"http://{Host}:{Port}";

    public async ValueTask InitializeAsync()
    {
        // Separate Docker/container-startup failures (→ skip all S3 tests) from post-startup
        // failures such as bucket creation or signing errors (→ fail loudly, real bug).
        try
        {
            IContainer container = new ContainerBuilder("pgsty/minio:latest")
                .WithCommand("server", "/data")
                .WithEnvironment("MINIO_ROOT_USER", AccessKey)
                .WithEnvironment("MINIO_ROOT_PASSWORD", SecretKey)
                .WithPortBinding(0, 9000)
                .WithWaitStrategy(Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(r => r.ForPath("/minio/health/live").ForPort(9000)))
                .WithLogger(new XunitMessageSinkLoggerProvider(messageSink).CreateLogger("testcontainers.minio"))
                .Build();

            await container.StartAsync();

            Host = container.Hostname;
            Port = container.GetMappedPublicPort(9000);
            _container = container;
        }
        catch
        {
            // Docker is unavailable or the container failed to start.
            // Mark as unavailable; each test will self-skip via Assert.Skip.
            DockerAvailable = false;
            return;
        }

        // The container is running.  Any failure here (bucket creation, SigV4 signing, …)
        // indicates a genuine defect rather than a missing Docker daemon, so let it propagate
        // and fail the test run loudly instead of silently skipping everything.
        await CreateBucketAsync();
        DockerAvailable = true;
    }

    private async Task CreateBucketAsync()
    {
        using S3Client s3Client = CreateS3Client();
        await s3Client.CreateBucketAsync(CancellationToken.None);
    }

    internal S3Client CreateS3Client()
    {
        var s3Options = new S3CacheOptions
        {
            Bucket = BucketName,
            Region = "us-east-1",
            ServiceUrl = Endpoint,
            AccessKey = AccessKey,
            SecretKey = SecretKey,
            UsePathStyle = true,
        };

        var cacheOptions = new MirrorOptions { Cache = new CacheOptions { Enabled = true, S3 = s3Options } };
        IOptions<MirrorOptions> options = Options.Create(cacheOptions);
        var handler = new SocketsHttpHandler();
        var httpClient = new HttpClient(handler);
        return new S3Client(httpClient, options, NullLogger<S3Client>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
