using DockerMirror.Configuration;
using DockerMirror.TestKit.Logger;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DockerMirror.IntegrationTests.TestInfrastructure;

public sealed class MirrorTestFactory : WebApplicationFactory<Program>, IAsyncDisposable
{
    private readonly ITestOutputHelper _testOutputHelper;
    private readonly Action<IWebHostBuilder>? _configureBuilder;
    private readonly TestDelegatingHandler _handler = new();
    private readonly string _cacheDir;
    private readonly bool _skipS3Handler;

    public MirrorTestFactory(ITestOutputHelper testOutputHelper) : this(testOutputHelper, cacheEnabled: true) { }

    public MirrorTestFactory(ITestOutputHelper testOutputHelper, bool cacheEnabled) : this(testOutputHelper, cacheEnabled, negativeCacheEnabled: true) { }

    public MirrorTestFactory(ITestOutputHelper testOutputHelper, Action<IWebHostBuilder> configureBuilder) : this(testOutputHelper, cacheEnabled: true, negativeCacheEnabled: true, configureBuilder) { }

    internal MirrorTestFactory(ITestOutputHelper testOutputHelper, bool cacheEnabled, bool negativeCacheEnabled)
        : this(testOutputHelper, cacheEnabled, negativeCacheEnabled, configureBuilder: null) { }

    internal MirrorTestFactory(
        ITestOutputHelper testOutputHelper,
        string s3ServiceUrl,
        string s3AccessKey,
        string s3SecretKey,
        string s3Bucket,
        bool cacheEnabled = true)
    {
        ArgumentNullException.ThrowIfNull(testOutputHelper);

        _testOutputHelper = testOutputHelper;
        _skipS3Handler = true;
        _cacheDir = string.Empty;

        _configureBuilder = builder =>
        {
            builder.UseSetting("Mirror:Cache:Enabled", cacheEnabled ? "true" : "false");
            builder.UseSetting("Mirror:Cache:Backend", "S3");

            builder.ConfigureTestServices(services =>
            {
                if (cacheEnabled)
                {
                    services.Configure<MirrorOptions>(o =>
                    {
                        o.Cache = new CacheOptions
                        {
                            Enabled = true,
                            Backend = "S3",
                            TagManifests = new TagManifestOptions { Enabled = true, Ttl = TimeSpan.FromMinutes(5) },
                            S3 = new S3CacheOptions
                            {
                                Bucket = s3Bucket,
                                Region = "us-east-1",
                                ServiceUrl = s3ServiceUrl,
                                AccessKey = s3AccessKey,
                                SecretKey = s3SecretKey,
                                UsePathStyle = true,
                            },
                            NegativeCache = new NegativeCacheOptions { Enabled = cacheEnabled, Ttl = TimeSpan.FromMinutes(5), MaxEntries = 10000 },
                        };
                    });
                }
                else
                {
                    services.Configure<MirrorOptions>(o => o.Cache = new CacheOptions { Enabled = false });
                }
            });
        };
    }

    internal MirrorTestFactory(ITestOutputHelper testOutputHelper, bool cacheEnabled, bool negativeCacheEnabled, Action<IWebHostBuilder>? configureBuilder)
    {
        _testOutputHelper = testOutputHelper;
        _configureBuilder = configureBuilder;
        _cacheDir = Path.Join(Path.GetTempPath(), "docker-mirror-integration-" + Guid.NewGuid().ToString("N"));
        if (cacheEnabled)
        {
            Directory.CreateDirectory(_cacheDir);
        }

        _configureBuilder = builder =>
        {
            builder.UseSetting("Mirror:Cache:Enabled", cacheEnabled ? "true" : "false");

            if (cacheEnabled)
            {
                builder.UseSetting("Mirror:Cache:FileSystem:Directory", _cacheDir);
            }

            builder.ConfigureTestServices(services =>
            {
                // ConfigureAppConfiguration in-memory overrides are not reliably propagated
                // to IOptions<T> when using WebApplication.CreateSlimBuilder. Use Configure<T>
                // to mutate the bound MirrorOptions snapshot explicitly. Configure callbacks
                // are additive — multiple calls merge into the same options instance, so the
                // factory's Cache settings and the later Upstream settings (registered in
                // ConfigureWebHost) coexist on a single MirrorOptions rather than overriding
                // each other as separate AddSingleton(IOptions<T>) registrations would.
                // Register unconditionally (both enabled and disabled) so the snapshot is
                // always authoritative and a change to the default value of CacheOptions.Enabled
                // cannot silently break the regression guard.
                if (cacheEnabled)
                {
                    services.Configure<MirrorOptions>(o =>
                    {
                        o.Cache = new CacheOptions
                        {
                            Enabled = true,
                            TagManifests = new TagManifestOptions { Enabled = true, Ttl = TimeSpan.FromMinutes(5) },
                            FileSystem = new FileSystemCacheOptions { Directory = _cacheDir },
                            NegativeCache = new NegativeCacheOptions { Enabled = negativeCacheEnabled, Ttl = TimeSpan.FromMinutes(5), MaxEntries = 10000 },
                        };
                    });
                }
                else
                {
                    services.Configure<MirrorOptions>(o => o.Cache = new CacheOptions { Enabled = false });
                }
            });

            configureBuilder?.Invoke(builder);
        };
    }

    public TestDelegatingHandler Handler => _handler;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Mirror:Upstream:RegistryUrl", "https://registry.test.local/");
        builder.UseSetting("Mirror:Upstream:TokenRealm", "https://auth.test.local/token");
        builder.UseSetting("Mirror:Upstream:TokenService", "registry.test.local");

        _configureBuilder?.Invoke(builder);

        builder.ConfigureLogging(loggingBuilder =>
        {
            loggingBuilder.ClearProviders();
            loggingBuilder.AddProvider(new XunitTestOutputLoggerProvider(_testOutputHelper));
        });

        builder.ConfigureTestServices(services =>
        {
            services.Configure<MirrorOptions>(o =>
            {
                o.Upstream = new UpstreamOptions
                {
                    RegistryUrl = "https://registry.test.local/",
                    TokenRealm = "https://auth.test.local/token",
                    TokenService = "registry.test.local",
                    Resilience = new UpstreamResilienceOptions { Enabled = false },
                };
            });

            services.AddSingleton<IHttpMessageHandlerBuilderFilter>(
                new TestHandlerBuilderFilter(() => _handler, _skipS3Handler));
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(false);

        _handler.Dispose();

        try
        {
            if (_cacheDir.Length > 0 && Directory.Exists(_cacheDir))
            {
                Directory.Delete(_cacheDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    private sealed class TestHandlerBuilderFilter(Func<HttpMessageHandler> handlerFactory, bool skipS3Handler) : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next)
        {
            return builder =>
            {
                next(builder);

                if (skipS3Handler && builder.Name == nameof(DockerMirror.Caching.S3.S3Client))
                {
                    return;
                }

                builder.PrimaryHandler = handlerFactory();
            };
        }
    }
}
