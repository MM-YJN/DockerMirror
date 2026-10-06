using DockerMirror.E2ETests.TestInfrastructure;
using DockerMirror.TestKit.Logger;

using DotNet.Testcontainers.Containers;

using Microsoft.Extensions.Logging;

namespace DockerMirror.E2ETests;

[Collection("E2E")]
public sealed class DockerPullEndToEndTests(E2EEnvironment env, ITestOutputHelper testOutputHelper)
{
    [Fact(Timeout = 300_000)]
    [Trait("Category", "E2E")]
    public async Task PullSucceedsAndExtracts()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var loggerFactory = new LoggerFactory([new XunitTestOutputLoggerProvider(testOutputHelper)]);

        // Arrange — unique repository so this test owns its cache/upstream state.
        SeededImage image = await env.SeedImageAsync("e2e/pull-extracts", "latest", cancellationToken);
        await using DinDContainer dind = await DinDContainer.CreateAsync(env.MirrorPort, loggerFactory, cancellationToken);
        string imageRef = $"host.testcontainers.internal:{env.MirrorPort}/{image.Reference}";

        // Act — pull.
        ExecResult pullResult = await dind.DockerPullAsync(imageRef, cancellationToken);

        // Assert — pull succeeded.
        Assert.True(pullResult.ExitCode == 0, $"docker pull failed. stderr: {pullResult.Stderr}");

        // Act — create a container then copy hello.txt out.  The synthesized
        // image is FROM scratch so there is no shell or cat; use docker cp
        // piped through tar -xO to extract the raw file content.
        ExecResult createResult = await dind.DockerExecAsync(["create", imageRef], cancellationToken);
        Assert.True(createResult.ExitCode == 0, $"docker create failed. stderr: {createResult.Stderr}");
        string containerId = createResult.Stdout.Trim();

        ExecResult cpResult = await dind.ExecRawAsync(
            ["sh", "-c", $"docker cp {containerId}:/hello.txt - | tar -xO"],
            cancellationToken);
        ExecResult rmResult = await dind.DockerExecAsync(["rm", containerId], cancellationToken);

        // Assert — output contains hello.
        Assert.True(cpResult.ExitCode == 0, $"docker cp failed. stderr: {cpResult.Stderr}");
        Assert.True(rmResult.ExitCode == 0, $"docker rm failed. stderr: {rmResult.Stderr}");
        Assert.Contains("hello", cpResult.Stdout, StringComparison.Ordinal);
    }

    [Fact(Timeout = 300_000)]
    [Trait("Category", "E2E")]
    public async Task SecondPullIsServedFromCache()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var loggerFactory = new LoggerFactory([new XunitTestOutputLoggerProvider(testOutputHelper)]);

        // Arrange — unique repository so the blob is guaranteed to be fetched by the
        // first pull below, never pre-warmed by a sibling test. Without this isolation
        // the before/after comparison could pass vacuously with both counts at zero.
        SeededImage image = await env.SeedImageAsync("e2e/cache-reuse", "latest", cancellationToken);
        await using DinDContainer dind = await DinDContainer.CreateAsync(env.MirrorPort, loggerFactory, cancellationToken);
        string imageRef = $"host.testcontainers.internal:{env.MirrorPort}/{image.Reference}";
        string blobPath = image.LayerBlobPath;

        // Act — first pull, which must fetch the layer blob from upstream.
        ExecResult firstPull = await dind.DockerPullAsync(imageRef, cancellationToken);
        Assert.True(firstPull.ExitCode == 0, $"first pull failed. stderr: {firstPull.Stderr}");

        int before = env.UpstreamRequestCounter.GetValueOrDefault(blobPath, 0);
        Assert.True(before > 0, $"expected the first pull to fetch {blobPath} from upstream, but it was never requested.");

        // Act — remove image then pull again.
        ExecResult rmiResult = await dind.DockerRmiAsync(imageRef, cancellationToken);
        Assert.True(rmiResult.ExitCode == 0, $"docker rmi failed. stderr: {rmiResult.Stderr}");

        ExecResult secondPull = await dind.DockerPullAsync(imageRef, cancellationToken);
        Assert.True(secondPull.ExitCode == 0, $"second pull failed. stderr: {secondPull.Stderr}");

        int after = env.UpstreamRequestCounter.GetValueOrDefault(blobPath, 0);

        // Assert — blob was not re-fetched from upstream.
        Assert.Equal(before, after);
    }

    [Fact(Timeout = 300_000)]
    [Trait("Category", "E2E")]
    public async Task LibraryRewritePullSucceeds()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var loggerFactory = new LoggerFactory([new XunitTestOutputLoggerProvider(testOutputHelper)]);

        // Arrange — registered as "library/testimage", pulled below via the
        // single-segment name "testimage" so DefaultNamespace rewriting is exercised.
        SeededImage image = await env.SeedImageAsync("library/testimage", "latest", cancellationToken);
        await using DinDContainer dind = await DinDContainer.CreateAsync(env.MirrorPort, loggerFactory, cancellationToken);
        string singleSegmentName = image.Repository[(image.Repository.LastIndexOf('/') + 1)..];
        string imageRef = $"host.testcontainers.internal:{env.MirrorPort}/{singleSegmentName}:{image.Tag}";

        // Act.
        ExecResult pullResult = await dind.DockerPullAsync(imageRef, cancellationToken);

        // Assert.
        Assert.True(pullResult.ExitCode == 0, $"docker pull with rewrite failed. stderr: {pullResult.Stderr}");
    }
}
