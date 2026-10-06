using System.Net;

using DockerMirror.Configuration;

using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

using Polly;
using Polly.CircuitBreaker;

namespace DockerMirror.Registry;

internal static class UpstreamResilienceExtensions
{
    internal const string ControlPlaneBreakerKey = "upstream-control-plane";
    internal const string RedirectBreakerKey = "upstream-redirect";

    public static IServiceCollection AddUpstreamResilience(this IServiceCollection services)
    {
        services.AddKeyedSingleton<ResiliencePipeline<HttpResponseMessage>>(
            ControlPlaneBreakerKey, (sp, _) => BuildBreaker(sp));

        services.AddKeyedSingleton<ResiliencePipeline<HttpResponseMessage>>(
            RedirectBreakerKey, (sp, _) => BuildBreaker(sp));

        return services;
    }

    public static IHttpClientBuilder AddUpstreamResilienceHandler(
        this IHttpClientBuilder builder, string handlerName, string breakerKey, bool streaming)
    {
        builder.AddResilienceHandler(handlerName, (pb, ctx) =>
        {
            UpstreamResilienceOptions o = ctx.ServiceProvider.GetRequiredService<IOptions<MirrorOptions>>().Value.Upstream.Resilience;

            if (!o.Enabled)
            {
                return;
            }

            ResiliencePipeline<HttpResponseMessage> breaker = ctx.ServiceProvider.GetRequiredKeyedService<ResiliencePipeline<HttpResponseMessage>>(breakerKey);

            if (streaming)
            {
                pb.AddTimeout(o.HeadersTimeout);
                pb.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = o.MaxRetryAttempts,
                    Delay = o.BaseDelay,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    ShouldHandle = UpstreamResiliencePredicates.CreateTransientPredicate(),
                });
                pb.AddPipeline(breaker);
            }
            else
            {
                pb.AddTimeout(o.TotalRequestTimeout);
                pb.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = o.MaxRetryAttempts,
                    Delay = o.BaseDelay,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    ShouldHandle = UpstreamResiliencePredicates.CreateTransientPredicate(),
                });
                pb.AddPipeline(breaker);
                pb.AddTimeout(o.AttemptTimeout);
            }
        });

        return builder;
    }

    public static SocketsHttpHandler CreatePrimaryHandler(IServiceProvider sp, bool allowAutoRedirect, UpstreamEndpoint endpoint)
    {
        UpstreamOptions options = sp.GetRequiredService<IOptions<MirrorOptions>>().Value.Upstream;

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = allowAutoRedirect,
            // Decompress any transport-level Content-Encoding (gzip/deflate/br) before the
            // bytes reach the caching layer. Without this, a transparent proxy/CDN that
            // compresses responses would cause the cached blob hash to be computed over the
            // compressed bytes, never matching the OCI digest, so every blob would fail to
            // commit and re-miss forever. HttpClient decompresses any recognized encoding
            // regardless of whether we advertised Accept-Encoding.
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = options.Resilience.ConnectTimeout,
        };

        ApplyProxy(handler, options.Proxy, endpoint);

        return handler;
    }

    private static void ApplyProxy(SocketsHttpHandler handler, ProxyOptions? proxy, UpstreamEndpoint endpoint)
    {
        string? url = proxy?.Resolve(endpoint);

        if (!string.IsNullOrEmpty(url))
        {
            var proxyUri = new Uri(url);
            var webProxy = new WebProxy(proxyUri);

            if (!string.IsNullOrEmpty(proxyUri.UserInfo))
            {
                webProxy.Credentials = ParseProxyCredentials(proxyUri.UserInfo);
            }

            handler.Proxy = webProxy;
            handler.UseProxy = true;
        }
    }

    private static NetworkCredential ParseProxyCredentials(ReadOnlySpan<char> userInfo)
    {
        Span<Range> parts = stackalloc Range[2];
        int numberOfRanges = userInfo.Split(parts, ':');
        ReadOnlySpan<char> username = userInfo[parts[0]];
        ReadOnlySpan<char> password = numberOfRanges > 1 ? userInfo[parts[1]] : default;
        return new NetworkCredential(username.ToString(), password.ToString());
    }

    private static ResiliencePipeline<HttpResponseMessage> BuildBreaker(IServiceProvider sp)
    {
        UpstreamResilienceOptions o = sp.GetRequiredService<IOptions<MirrorOptions>>().Value.Upstream.Resilience;

        return new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
            {
                FailureRatio = o.FailureRatio,
                SamplingDuration = o.SamplingDuration,
                MinimumThroughput = o.MinimumThroughput,
                BreakDuration = o.BreakDuration,
                ShouldHandle = UpstreamResiliencePredicates.CreateTransientPredicate(),
            })
            .Build();
    }
}
