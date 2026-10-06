using System.Diagnostics;
using System.Globalization;
using System.Reflection;

using DockerMirror.Caching;
using DockerMirror.Configuration;
using DockerMirror.Diagnostics;
using DockerMirror.Json;

using Microsoft.Extensions.Options;

namespace DockerMirror.Registry;

public static class AdminEndpoints
{
    private static readonly string s_appVersion =
        typeof(Program).Assembly
            .GetCustomAttributes<AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()
            ?.InformationalVersion ?? string.Empty;

    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        MirrorOptions options = app.Services.GetRequiredService<IOptions<MirrorOptions>>().Value;

        if (!options.Admin.Enabled)
        {
            return;
        }

        string basePath = options.NormalizedBasePath;
        IEndpointRouteBuilder target = string.IsNullOrEmpty(basePath) ? app : app.MapGroup(basePath);
        target.MapGet(options.Admin.Path, AdminStatsHandlerAsync);
    }

    private static async Task<IResult> AdminStatsHandlerAsync(
        IOptions<MirrorOptions> options,
        CacheStatsState cacheStats,
        NegativeCache negativeCache,
        IContentStore store)
    {
        UpstreamOptions upstream = options.Value.Upstream;
        CacheOptions cache = options.Value.Cache;

        var response = new MirrorStatsResponse
        {
            Version = GetVersion(),
            Uptime = GetUptime(),
            Upstream = new UpstreamStats
            {
                RegistryUrl = upstream.RegistryUrl,
                TokenService = upstream.TokenService,
                DefaultNamespace = upstream.DefaultNamespace,
            },
            Cache = new CacheStats
            {
                Enabled = cache.Enabled,
                Backend = cache.Backend,
                EvictionEnabled = cache.Eviction.Enabled,
                NegativeCacheEnabled = cache.NegativeCache.Enabled,
                TagManifests = new TagManifestStats
                {
                    Enabled = cache.TagManifests.Enabled,
                    Ttl = cache.TagManifests.Ttl,
                },
                SizeBytes = cacheStats.SizeBytes,
                EntryCount = cacheStats.EntryCount,
                LastUpdatedUtc = cacheStats.LastUpdateUtc != DateTimeOffset.MinValue
                    ? cacheStats.LastUpdateUtc.ToString("O", CultureInfo.InvariantCulture)
                    : null,
                NegativeCacheEntries = negativeCache.Count,
            },
            Storage = await ProbeStorageAsync(store).ConfigureAwait(false),
        };

        return Results.Json(response, RegistryJsonContext.Default.MirrorStatsResponse, "application/json", StatusCodes.Status200OK);
    }

    private static async ValueTask<StorageStats> ProbeStorageAsync(IContentStore store)
    {
        if (store is not IStorageHealthProbe probe)
        {
            return new StorageStats();
        }

        try
        {
            await probe.CheckAsync(CancellationToken.None).ConfigureAwait(false);
            return new StorageStats { Healthy = true };
        }
        catch (Exception ex)
        {
            return new StorageStats { Healthy = false, Error = ex.Message };
        }
    }

    private static string GetVersion() => s_appVersion;

    private static string GetUptime()
    {
        using var proc = Process.GetCurrentProcess();
        DateTime startTime = proc.StartTime.ToUniversalTime();
        return (DateTimeOffset.UtcNow - startTime).ToString("g");
    }
}
