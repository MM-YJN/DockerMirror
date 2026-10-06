using System.Collections.Concurrent;

using DockerMirror.Configuration;
using DockerMirror.Registry;
using DockerMirror.TestKit.Logger;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Xunit.Sdk;

namespace DockerMirror.E2ETests.TestInfrastructure;

public sealed class E2EEnvironment(IMessageSink messageSink) : IAsyncLifetime
{
    private readonly ConcurrentDictionary<string, SeededImage> _seededImages = new(StringComparer.Ordinal);
    private ILoggerFactory? _loggerFactory;
    private IContainer? _registry;
    private WebApplicationFactory<Program>? _factory;
    private string? _cacheDir;

    public int MirrorPort { get; private set; }

    public string RegistryBaseUrl { get; private set; } = string.Empty;

    public ConcurrentDictionary<string, int> UpstreamRequestCounter { get; } = new();

    // Seeds (once) and returns an image under a repository unique to the caller, so
    // tests sharing this collection fixture never observe each other's traffic or
    // cache state. Each repository is registered with a distinct tag.
    public async ValueTask<SeededImage> SeedImageAsync(string repository, string tag, CancellationToken ct)
    {
        string key = $"{repository}:{tag}";

        if (_seededImages.TryGetValue(key, out SeededImage? existing))
        {
            return existing;
        }

        SeededImage seeded = await RegistrySeeder.SeedAsync(RegistryBaseUrl, repository, tag, ct).ConfigureAwait(false);
        _seededImages[key] = seeded;
        return seeded;
    }

    public async ValueTask InitializeAsync()
    {
        // Use a timeout-based token rather than TestContext.Current.CancellationToken,
        // which is tied to a specific test and is not available during fixture initialization.
        using var startupCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        CancellationToken cancellationToken = startupCts.Token;

        var synchronizedMessageSink = new SynchronizedMessageSink(messageSink);
        _loggerFactory = new LoggerFactory([new XunitMessageSinkLoggerProvider(synchronizedMessageSink)]);

        ILogger registryLogger = _loggerFactory.CreateLogger("testcontainers.registry");

        // 1. Start registry:3 container.
        _registry = new ContainerBuilder("registry:3")
            .WithPortBinding(0, 5000)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPath("/v2/").ForPort(5000)))
            .WithLogger(registryLogger)
            .Build();

        await _registry.StartAsync(cancellationToken);

        string registryHost = _registry.Hostname;
        ushort registryPort = _registry.GetMappedPublicPort(5000);

        RegistryBaseUrl = $"http://{registryHost}:{registryPort}";

        // 2. Build the mirror.
        _cacheDir = Path.Join(Path.GetTempPath(), "docker-mirror-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_cacheDir);

        ConcurrentDictionary<string, int> counter = UpstreamRequestCounter;

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration(config =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["HTTP_PORTS"] = string.Empty,
                        ["HTTPS_PORTS"] = string.Empty,
                    });
                });

                builder.ConfigureServices(services =>
                {
                    services.Configure<MirrorOptions>(o =>
                    {
                        o.Upstream = new UpstreamOptions
                        {
                            RegistryUrl = $"http://{registryHost}:{registryPort}/",
                            TokenRealm = $"http://localhost/token",
                            TokenService = "e2e",
                            Resilience = new UpstreamResilienceOptions { Enabled = false },
                        };
                        o.Cache = new CacheOptions
                        {
                            Enabled = true,
                            Backend = "FileSystem",
                            FileSystem = new FileSystemCacheOptions { Directory = _cacheDir },
                            TagManifests = new TagManifestOptions { Enabled = true, Ttl = TimeSpan.FromMinutes(5) },
                        };
                    });

                    // Override UpstreamTokenService primary handler to return a static token.
                    services.AddHttpClient<UpstreamTokenService>()
                        .ConfigurePrimaryHttpMessageHandler(() => new DummyTokenHandler());

                    // Append RecordingHandler to count upstream requests by path.
                    services.AddHttpClient<UpstreamRegistryClient>()
                        .AddHttpMessageHandler(() => new RecordingHandler(counter));
                });

                builder.ConfigureLogging(loggingBuilder =>
                {
                    loggingBuilder.ClearProviders();
                    loggingBuilder.AddProvider(new XunitMessageSinkLoggerProvider(synchronizedMessageSink));
                });
            });

        _factory.UseKestrel(o => o.ListenAnyIP(0));
        _factory.StartServer();

        // Read the actual port.
        IServer server = _factory.Services.GetRequiredService<IServer>();
        IServerAddressesFeature addresses = server.Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("IServerAddressesFeature not available.");
        string addr = addresses.Addresses.First();
        MirrorPort = new Uri(addr).Port;

        // 3. Expose the mirror port to containers.
        await TestcontainersSettings.ExposeHostPortsAsync([(ushort)MirrorPort], cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync().ConfigureAwait(false);
        }

        _loggerFactory?.Dispose();

        if (_registry is not null)
        {
            await _registry.DisposeAsync().ConfigureAwait(false);
        }

        if (_cacheDir is not null)
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
                // Best-effort cleanup.
            }
        }
    }
}
