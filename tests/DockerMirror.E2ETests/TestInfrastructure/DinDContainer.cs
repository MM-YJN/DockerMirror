using System.Text;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

using Microsoft.Extensions.Logging;

namespace DockerMirror.E2ETests.TestInfrastructure;

public sealed class DinDContainer : IAsyncDisposable
{
    private readonly IContainer _container;

    private DinDContainer(IContainer container) => _container = container;

    public static async Task<DinDContainer> CreateAsync(int mirrorPort, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        byte[] daemonJson = Encoding.UTF8.GetBytes(
            $$"""{"insecure-registries":["host.testcontainers.internal:{{mirrorPort}}"]}""");

        ILogger logger = loggerFactory.CreateLogger("testcontainers.dind");

        IContainer container = new ContainerBuilder("docker:29-dind")
            .WithPrivileged(true)
            .WithEnvironment("DOCKER_TLS_CERTDIR", "")
            .WithResourceMapping(daemonJson, "/etc/docker/daemon.json")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilCommandIsCompleted(["docker", "info"]))
            .WithLogger(logger)
            .Build();

        await container.StartAsync(ct).ConfigureAwait(false);

        return new DinDContainer(container);
    }

    public async Task<ExecResult> DockerPullAsync(string imageRef, CancellationToken ct)
    {
        return await _container.ExecAsync(["docker", "pull", imageRef], ct).ConfigureAwait(false);
    }

    public async Task<ExecResult> DockerRmiAsync(string imageRef, CancellationToken ct)
    {
        return await _container.ExecAsync(["docker", "rmi", imageRef], ct).ConfigureAwait(false);
    }

    public async Task<ExecResult> DockerExecAsync(string[] args, CancellationToken ct)
    {
        string[] fullArgs = new string[1 + args.Length];
        fullArgs[0] = "docker";
        Array.Copy(args, 0, fullArgs, 1, args.Length);
        return await _container.ExecAsync(fullArgs, ct).ConfigureAwait(false);
    }

    public async Task<ExecResult> ExecRawAsync(string[] args, CancellationToken ct)
    {
        return await _container.ExecAsync(args, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync().ConfigureAwait(false);
    }
}
