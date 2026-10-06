using System.Net;

using DockerMirror.Diagnostics.HealthChecks;
using DockerMirror.UnitTests.TestInfrastructure;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;

namespace DockerMirror.UnitTests.Diagnostics.HealthChecks;

public sealed class UpstreamHealthCheckTests
{
    [Fact]
    public async Task ReturnsHealthy_WhenUpstreamReturns200()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var handler = new TestHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://registry.test/") };
        var check = new UpstreamHealthCheck(http, NullLogger<UpstreamHealthCheck>.Instance);

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext(), ct);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task ReturnsHealthy_WhenUpstreamReturns401()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var handler = new TestHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://registry.test/") };
        var check = new UpstreamHealthCheck(http, NullLogger<UpstreamHealthCheck>.Instance);

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext(), ct);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task ReturnsHealthy_WhenUpstreamReturns403()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var handler = new TestHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://registry.test/") };
        var check = new UpstreamHealthCheck(http, NullLogger<UpstreamHealthCheck>.Instance);

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext(), ct);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task ReturnsUnhealthy_WhenUpstreamReturns503()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var handler = new TestHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://registry.test/") };
        var check = new UpstreamHealthCheck(http, NullLogger<UpstreamHealthCheck>.Instance);

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext(), ct);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task ReturnsUnhealthy_WhenUpstreamUnreachable()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var crashHandler = new TestHttpMessageHandler(_ =>
            throw new HttpRequestException("Upstream unreachable"));
        using var http = new HttpClient(crashHandler) { BaseAddress = new Uri("https://registry.test/") };
        var check = new UpstreamHealthCheck(http, NullLogger<UpstreamHealthCheck>.Instance);

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext(), ct);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }
}
