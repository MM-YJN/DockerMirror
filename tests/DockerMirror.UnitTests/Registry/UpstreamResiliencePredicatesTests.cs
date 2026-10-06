using System.Net;

using DockerMirror.Registry;

using Polly;
using Polly.Retry;

namespace DockerMirror.UnitTests.Registry;

public sealed class UpstreamResiliencePredicatesTests
{
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public void IsTransientStatusCode_ReturnsTrueForTransient(HttpStatusCode statusCode)
    {
        Assert.True(UpstreamResiliencePredicates.IsTransientStatusCode(statusCode));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.NotModified)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.Gone)]
    public void IsTransientStatusCode_ReturnsFalseForNonTransient(HttpStatusCode statusCode)
    {
        Assert.False(UpstreamResiliencePredicates.IsTransientStatusCode(statusCode));
    }

    [Fact]
    public void IsTransientStatusCode_All5xxAreTransient()
    {
        for (int code = 500; code <= 599; code++)
        {
            Assert.True(UpstreamResiliencePredicates.IsTransientStatusCode((HttpStatusCode)code),
                $"Expected {(HttpStatusCode)code} to be transient");
        }
    }

    [Fact]
    public async Task RetryPredicate_RetriesOnTransientStatusCode()
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
    public async Task RetryPredicate_DoesNotRetryOnNonTransientStatusCode()
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
    public async Task RetryPredicate_RetriesOnHttpRequestException()
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
    public async Task RetryPredicate_RetriesOnConnectionTimeout()
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
}
