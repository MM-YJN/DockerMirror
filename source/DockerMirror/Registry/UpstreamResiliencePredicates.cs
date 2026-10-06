using System.Net;

using Polly;
using Polly.Timeout;

namespace DockerMirror.Registry;

internal static class UpstreamResiliencePredicates
{
    public static PredicateBuilder<HttpResponseMessage> CreateTransientPredicate()
    {
        return new PredicateBuilder<HttpResponseMessage>()
            .Handle<HttpRequestException>()
            .Handle<TimeoutRejectedException>()
            .Handle<OperationCanceledException>(ex => ex.InnerException is TimeoutException)
            .HandleResult(r => IsTransientStatusCode(r.StatusCode));
    }

    internal static bool IsTransientStatusCode(HttpStatusCode code)
    {
        int statusCode = (int)code;

        return statusCode is >= 500 or 408 or 429;
    }
}
