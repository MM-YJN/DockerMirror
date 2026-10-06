using System.Diagnostics.CodeAnalysis;
using System.Reflection;

using DockerMirror.Caching;
using DockerMirror.Configuration;
using DockerMirror.Diagnostics;
using DockerMirror.Diagnostics.HealthChecks;
using DockerMirror.Registry;
using DockerMirror.ServiceDefaults;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace DockerMirror;

[SuppressMessage("Maintainability", "CA1506: Avoid excessive class coupling", Justification = "This is necessary to configure needed services.")]
public sealed partial class Program
{
    private static Task Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);

        builder.AddServiceDefaults();

        builder.Services.AddOptions<MirrorOptions>()
            .Bind(builder.Configuration.GetSection(MirrorOptions.SectionName))
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsValidator>();
        builder.Services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsUpstreamValidator>();
        builder.Services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsBasePathValidator>();
        builder.Services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsS3Validator>();
        builder.Services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsCacheValidator>();
        builder.Services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsResilienceValidator>();

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<MirrorMetrics>();
        builder.Services.AddSingleton<CacheStatsState>();

        builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter(MirrorMetrics.MeterName));

        builder.Services.AddUpstreamResilience();

        builder.Services.AddHttpClient<UpstreamTokenService>((services, client) =>
        {
            UpstreamOptions options = services.GetRequiredService<IOptions<MirrorOptions>>().Value.Upstream;
            client.BaseAddress = new Uri(options.TokenRealm);
        })
            .AddUpstreamResilienceHandler("upstream-token", UpstreamResilienceExtensions.ControlPlaneBreakerKey, streaming: false)
            .ConfigurePrimaryHttpMessageHandler(sp => UpstreamResilienceExtensions.CreatePrimaryHandler(sp, allowAutoRedirect: true, UpstreamEndpoint.Auth));

        builder.Services.AddTransient<TokenAuthHandler>();

        builder.Services.AddHttpClient<UpstreamRegistryClient>((services, client) =>
        {
            UpstreamOptions options = services.GetRequiredService<IOptions<MirrorOptions>>().Value.Upstream;
            client.BaseAddress = new Uri(options.RegistryUrl.TrimEnd('/') + "/");
        })
            .AddHttpMessageHandler<TokenAuthHandler>()
            .AddUpstreamResilienceHandler("upstream-registry", UpstreamResilienceExtensions.ControlPlaneBreakerKey, streaming: true)
            .ConfigurePrimaryHttpMessageHandler(sp => UpstreamResilienceExtensions.CreatePrimaryHandler(sp, allowAutoRedirect: false, UpstreamEndpoint.Registry));

        // Dedicated token‑less client for the upstream readiness probe.  The
        // TokenAuthHandler cannot handle bare /v2/ paths, so this client is
        // configured without it.  Reads proxy/TLS settings via the shared
        // CreatePrimaryHandler helper so the probe respects the production network
        // configuration.
        builder.Services.AddHttpClient<UpstreamHealthCheck>((sp, client) =>
        {
            UpstreamOptions o = sp.GetRequiredService<IOptions<MirrorOptions>>().Value.Upstream;
            client.BaseAddress = new Uri(o.RegistryUrl.TrimEnd('/') + "/");
        })
            .ConfigurePrimaryHttpMessageHandler(sp =>
                UpstreamResilienceExtensions.CreatePrimaryHandler(sp, allowAutoRedirect: false, UpstreamEndpoint.Registry));

        // CachingRegistryService and its collaborators are always registered. When
        // Mirror:Cache:Enabled=false the handlers short-circuit at the router level.
        builder.Services.AddScoped<CachingRegistryService>();

        RegisterContentCache(builder.Services, builder.Configuration);

        builder.Services.AddHealthChecks()
            .AddCheck<UpstreamHealthCheck>("upstream", failureStatus: HealthStatus.Unhealthy, tags: ["ready"], timeout: TimeSpan.FromSeconds(5))
            .AddCheck<StorageHealthCheck>("storage", failureStatus: HealthStatus.Unhealthy, tags: ["ready"], timeout: TimeSpan.FromSeconds(5));

        WebApplication app = builder.Build();

        RegistryEndpoints.Map(app);
        AdminEndpoints.Map(app);

        app.MapDefaultEndpoints();

        PrintVersion(app.Services.GetRequiredService<ILogger<Program>>());

        return app.RunAsync();
    }

    internal static void RegisterContentCache(IServiceCollection services, ConfigurationManager configuration)
    {
        // Register all cache infrastructure unconditionally so DI composition succeeds
        // regardless of the Mirror:Cache:Enabled config value.  Runtime behaviour is
        // gated inside each service via IOptions<MirrorOptions>:
        //   - CachingRegistryService  – forwards when Enabled=false
        //   - CacheWarmingService     – returns immediately from ExecuteAsync when Enabled=false
        //   - FileSystemCachePreflightService – no-ops in StartingAsync when Enabled=false
        //     (only registered for FileSystem backend; S3 uses S3CachePreflightService)
        //   - CacheEvictionService    – no-ops when Enabled=false or Eviction:Enabled=false
        //
        // Use the standard config binder to read composition-time settings from our options
        // classes.  The configuration-binding source generator (EnableConfigurationBindingGenerator)
        // supports init-only properties via object-initializer syntax in the generated code.
        MirrorOptions mirrorOptions = configuration.GetSection(MirrorOptions.SectionName).Get<MirrorOptions>() ?? new MirrorOptions();
        string backend = mirrorOptions.Cache.Backend;

        if (string.Equals(backend, "S3", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHttpClient<Caching.S3.S3Client>()
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                });

            services.AddSingleton<S3ContentStore>();
            services.AddSingleton<IContentStore>(sp => sp.GetRequiredService<S3ContentStore>());
            services.AddSingleton<ITagPointerStore>(sp => sp.GetRequiredService<S3ContentStore>());
            services.AddHostedService<S3CachePreflightService>();
        }
        else
        {
            services.AddSingleton<FileSystemContentStore>();
            services.AddSingleton<IContentStore>(sp => sp.GetRequiredService<FileSystemContentStore>());
            services.AddSingleton<ITagPointerStore>(sp => sp.GetRequiredService<FileSystemContentStore>());
            services.AddHostedService<FileSystemCachePreflightService>();
        }

        services.AddSingleton<KeyedAsyncLock>();
        services.AddSingleton<CacheWarmingQueue>();
        services.AddSingleton<NegativeCache>();
        services.AddSingleton<NegativeCacheGate>();
        services.AddSingleton<CacheResponder>();
        services.AddSingleton<DigestResourceHandler>();
        services.AddSingleton<TagManifestHandler>();
        services.AddSingleton<TagsListResponseCache>();
        services.AddSingleton<CatalogListResponseCache>();
        services.AddSingleton<ListResourceHandler>();

        services.AddHostedService<CacheWarmingService>();
        services.AddHostedService<CacheEvictionService>();

        services.AddHttpClient("upstream-redirect")
            .AddUpstreamResilienceHandler("upstream-redirect-handler", UpstreamResilienceExtensions.RedirectBreakerKey, streaming: true)
            .ConfigurePrimaryHttpMessageHandler(sp => UpstreamResilienceExtensions.CreatePrimaryHandler(sp, allowAutoRedirect: true, UpstreamEndpoint.Cdn));
    }

    private static void PrintVersion(ILogger logger)
    {
        Assembly assembly = typeof(Program).Assembly;
        string? assemblyVersion = assembly
            .GetCustomAttributes<AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?
            .InformationalVersion;

        LogApplicationStarted(logger, assemblyVersion ?? string.Empty);
    }

    [LoggerMessage(LogLevel.Information, "DockerMirror started. Version: {version}")]
    private static partial void LogApplicationStarted(ILogger logger, string version);
}
