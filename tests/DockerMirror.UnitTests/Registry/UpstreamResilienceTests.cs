using System.Net;

using DockerMirror.Configuration;
using DockerMirror.Registry;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace DockerMirror.UnitTests.Registry;

public sealed class UpstreamResilienceTests
{
    [Fact]
    public void ValidateOptions_FailsOnInvalidFailureRatio()
    {
        var services = new ServiceCollection();
        services.AddOptions<MirrorOptions>()
            .Configure(o =>
            {
                o.Upstream.RegistryUrl = "https://registry-1.docker.io";
                o.Upstream.TokenRealm = "https://auth.docker.io/token";
                o.Upstream.TokenService = "registry.docker.io";
                o.Upstream.Resilience.FailureRatio = 2.0;
            });
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsValidator>();
        using ServiceProvider sp = services.BuildServiceProvider();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<IOptions<MirrorOptions>>().Value);

        Assert.Contains("FailureRatio", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateOptions_FailsOnInvalidMaxRetryAttempts()
    {
        var services = new ServiceCollection();
        services.AddOptions<MirrorOptions>()
            .Configure(o =>
            {
                o.Upstream.RegistryUrl = "https://registry-1.docker.io";
                o.Upstream.TokenRealm = "https://auth.docker.io/token";
                o.Upstream.TokenService = "registry.docker.io";
                o.Upstream.Resilience.MaxRetryAttempts = 20;
            });
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsValidator>();
        using ServiceProvider sp = services.BuildServiceProvider();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<IOptions<MirrorOptions>>().Value);

        Assert.Contains("MaxRetryAttempts", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateOptions_FailsOnInvalidMinimumThroughput()
    {
        var services = new ServiceCollection();
        services.AddOptions<MirrorOptions>()
            .Configure(o =>
            {
                o.Upstream.RegistryUrl = "https://registry-1.docker.io";
                o.Upstream.TokenRealm = "https://auth.docker.io/token";
                o.Upstream.TokenService = "registry.docker.io";
                o.Upstream.Resilience.MinimumThroughput = 1;
            });
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsValidator>();
        using ServiceProvider sp = services.BuildServiceProvider();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<IOptions<MirrorOptions>>().Value);

        Assert.Contains("MinimumThroughput", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateOptions_PassesOnValidConfig()
    {
        var services = new ServiceCollection();
        services.AddOptions<MirrorOptions>()
            .Configure(o =>
            {
                o.Upstream.RegistryUrl = "https://registry-1.docker.io";
                o.Upstream.TokenRealm = "https://auth.docker.io/token";
                o.Upstream.TokenService = "registry.docker.io";
                o.Upstream.Resilience.FailureRatio = 0.1;
                o.Upstream.Resilience.MaxRetryAttempts = 3;
                o.Upstream.Resilience.MinimumThroughput = 10;
            });
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsValidator>();
        using ServiceProvider sp = services.BuildServiceProvider();

        // Accessing .Value triggers all registered IValidateOptions<T> implementations.
        // No OptionsValidationException means validation passed.
        MirrorOptions options = sp.GetRequiredService<IOptions<MirrorOptions>>().Value;
        Assert.NotNull(options);
    }

    [Fact]
    public async Task RetryPipeline_RetriesOnTransientAndSucceeds()
    {
        int attempts = 0;
        ResiliencePipeline<HttpResponseMessage> pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.Zero,
                ShouldHandle = UpstreamResiliencePredicates.CreateTransientPredicate(),
            })
            .Build();

        HttpResponseMessage result = await pipeline.ExecuteAsync(ct =>
        {
            int current = Interlocked.Increment(ref attempts);
            if (current <= 2)
            {
                return ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            return ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task RetryPipeline_DoesNotRetryOnNonTransient()
    {
        int attempts = 0;
        ResiliencePipeline<HttpResponseMessage> pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.Zero,
                ShouldHandle = UpstreamResiliencePredicates.CreateTransientPredicate(),
            })
            .Build();

        HttpResponseMessage result = await pipeline.ExecuteAsync(ct =>
        {
            Interlocked.Increment(ref attempts);
            return ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task RetryPipeline_RetriesOnHttpRequestException()
    {
        int attempts = 0;
        ResiliencePipeline<HttpResponseMessage> pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 2,
                Delay = TimeSpan.Zero,
                ShouldHandle = UpstreamResiliencePredicates.CreateTransientPredicate(),
            })
            .Build();

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            pipeline.ExecuteAsync<HttpResponseMessage>(ct =>
                {
                    Interlocked.Increment(ref attempts);
                    throw new HttpRequestException("Network error");
                }, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task RetryPipeline_RetriesOnConnectionTimeout()
    {
        int attempts = 0;
        ResiliencePipeline<HttpResponseMessage> pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 2,
                Delay = TimeSpan.Zero,
                ShouldHandle = UpstreamResiliencePredicates.CreateTransientPredicate(),
            })
            .Build();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            pipeline.ExecuteAsync<HttpResponseMessage>(ct =>
                {
                    Interlocked.Increment(ref attempts);
                    throw new OperationCanceledException("Connection timed out", new TimeoutException());
                }, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task RetryPipeline_HonorsRetryAfterHeader()
    {
        int attempts = 0;
        var retryAfterDelay = TimeSpan.FromMilliseconds(200);
        ResiliencePipeline<HttpResponseMessage> pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(50),
                ShouldHandle = UpstreamResiliencePredicates.CreateTransientPredicate(),
                ShouldRetryAfterHeader = true,
            })
            .Build();

        DateTimeOffset started = DateTimeOffset.UtcNow;

        HttpResponseMessage result = await pipeline.ExecuteAsync(ct =>
        {
            Interlocked.Increment(ref attempts);
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfterDelay);
            return ValueTask.FromResult(response);
        }, TestContext.Current.CancellationToken);

        TimeSpan elapsed = DateTimeOffset.UtcNow - started;

        Assert.Equal(HttpStatusCode.TooManyRequests, result.StatusCode);
        Assert.Equal(4, attempts);
        Assert.True(elapsed >= retryAfterDelay * 3,
            $"Expected total elapsed >= {retryAfterDelay * 3} but was {elapsed}");
    }

    [Fact]
    public async Task CircuitBreaker_OpensAfterThreshold()
    {
        ResiliencePipeline<HttpResponseMessage> breaker = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
            {
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 4,
                BreakDuration = TimeSpan.FromMinutes(1),
                ShouldHandle = UpstreamResiliencePredicates.CreateTransientPredicate(),
            })
            .Build();

        int failures = 0;

        while (true)
        {
            try
            {
                await breaker.ExecuteAsync(ct =>
                    {
                        failures++;
                        return ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                    }, TestContext.Current.CancellationToken);
            }
            catch (BrokenCircuitException)
            {
                break;
            }
        }

        Assert.True(failures >= 2,
            $"Breaker should have opened after at least 2 failures but had {failures}");
    }

    [Fact]
    public async Task CircuitBreaker_AffectsAllConsumersInSamePipeline()
    {
        ResiliencePipeline<HttpResponseMessage> breaker = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
            {
                FailureRatio = 0.6,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 5,
                BreakDuration = TimeSpan.FromMinutes(1),
                ShouldHandle = UpstreamResiliencePredicates.CreateTransientPredicate(),
            })
            .Build();

        ResiliencePipeline<HttpResponseMessage> pipelineA = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 2,
                Delay = TimeSpan.Zero,
                ShouldHandle = UpstreamResiliencePredicates.CreateTransientPredicate(),
            })
            .AddPipeline(breaker)
            .Build();

        ResiliencePipeline<HttpResponseMessage> pipelineB = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddPipeline(breaker)
            .Build();

        int aFailures = 0;

        while (true)
        {
            try
            {
                await pipelineA.ExecuteAsync(ct =>
                    {
                        aFailures++;
                        return ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                    }, TestContext.Current.CancellationToken);
            }
            catch (BrokenCircuitException)
            {
                break;
            }
            catch (HttpRequestException)
            {
            }
        }

        await Assert.ThrowsAsync<BrokenCircuitException>(() =>
            pipelineB.ExecuteAsync(ct =>
                ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.OK)),
                TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public void AddUpstreamResilience_RegistersTwoDistinctKeyedBreakers()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new MirrorOptions
        {
            Upstream = new UpstreamOptions
            {
                RegistryUrl = "https://registry-1.docker.io",
                TokenRealm = "https://auth.docker.io/token",
                TokenService = "registry.docker.io",
            },
        }));
        services.AddUpstreamResilience();

        using ServiceProvider sp = services.BuildServiceProvider();

        ResiliencePipeline<HttpResponseMessage> controlPlane = sp.GetRequiredKeyedService<ResiliencePipeline<HttpResponseMessage>>(
            UpstreamResilienceExtensions.ControlPlaneBreakerKey);
        ResiliencePipeline<HttpResponseMessage> redirect = sp.GetRequiredKeyedService<ResiliencePipeline<HttpResponseMessage>>(
            UpstreamResilienceExtensions.RedirectBreakerKey);

        Assert.NotNull(controlPlane);
        Assert.NotNull(redirect);
        Assert.NotSame(controlPlane, redirect);
    }

    [Fact]
    public async Task KeyedBreakers_IsolateFailures()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new MirrorOptions
        {
            Upstream = new UpstreamOptions
            {
                RegistryUrl = "https://registry-1.docker.io",
                TokenRealm = "https://auth.docker.io/token",
                TokenService = "registry.docker.io",
                Resilience = new UpstreamResilienceOptions
                {
                    FailureRatio = 0.6,
                    SamplingDuration = TimeSpan.FromSeconds(30),
                    MinimumThroughput = 5,
                    BreakDuration = TimeSpan.FromMinutes(1),
                },
            },
        }));
        services.AddUpstreamResilience();

        using ServiceProvider sp = services.BuildServiceProvider();
        ResiliencePipeline<HttpResponseMessage> controlPlane = sp.GetRequiredKeyedService<ResiliencePipeline<HttpResponseMessage>>(
            UpstreamResilienceExtensions.ControlPlaneBreakerKey);
        ResiliencePipeline<HttpResponseMessage> redirect = sp.GetRequiredKeyedService<ResiliencePipeline<HttpResponseMessage>>(
            UpstreamResilienceExtensions.RedirectBreakerKey);

        Assert.NotSame(controlPlane, redirect);

        // Trigger the redirect breaker to open.
        while (true)
        {
            try
            {
                await redirect.ExecuteAsync(ct =>
                        ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
                    TestContext.Current.CancellationToken);
            }
            catch (BrokenCircuitException)
            {
                break;
            }
        }

        // The redirect breaker is open, but the control-plane breaker remains closed.
        HttpResponseMessage result = await controlPlane.ExecuteAsync(ct =>
            ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.OK)),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
    }
}
