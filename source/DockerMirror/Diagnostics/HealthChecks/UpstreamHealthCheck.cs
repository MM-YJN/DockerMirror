using System.Net;

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DockerMirror.Diagnostics.HealthChecks;

internal sealed partial class UpstreamHealthCheck(
    HttpClient http,
    ILogger<UpstreamHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "v2/");
            request.Headers.Host = null;

            using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return HealthCheckResult.Healthy("Upstream registry is reachable.");
            }

            LogUpstreamNonSuccess((int)response.StatusCode);
            return HealthCheckResult.Unhealthy($"Upstream registry returned status {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Upstream registry probe timed out.");
        }
        catch (Exception ex)
        {
            LogUpstreamUnavailable(ex);
            return HealthCheckResult.Unhealthy("Upstream registry is unavailable.", ex);
        }
    }

    [LoggerMessage(LogLevel.Warning, "Upstream registry health probe returned status {StatusCode}.")]
    private partial void LogUpstreamNonSuccess(int statusCode);

    [LoggerMessage(LogLevel.Error, "Upstream registry health probe failed.")]
    private partial void LogUpstreamUnavailable(Exception ex);
}
