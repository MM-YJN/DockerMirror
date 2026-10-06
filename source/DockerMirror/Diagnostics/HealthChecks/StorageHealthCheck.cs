using DockerMirror.Caching;
using DockerMirror.Configuration;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace DockerMirror.Diagnostics.HealthChecks;

internal sealed partial class StorageHealthCheck(
    IContentStore store,
    IOptions<MirrorOptions> options,
    ILogger<StorageHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct)
    {
        if (!options.Value.Cache.Enabled)
        {
            return HealthCheckResult.Healthy("Caching is disabled.");
        }

        if (store is IStorageHealthProbe probe)
        {
            try
            {
                await probe.CheckAsync(ct).ConfigureAwait(false);
                return HealthCheckResult.Healthy("Storage backend is reachable.");
            }
            catch (Exception ex)
            {
                LogStorageUnavailable(ex);
                return HealthCheckResult.Unhealthy("Storage backend is unavailable.", ex);
            }
        }

        return HealthCheckResult.Healthy("Storage probe not available (test stub).");
    }

    [LoggerMessage(LogLevel.Error, "Storage backend health check failed.")]
    private partial void LogStorageUnavailable(Exception ex);
}
