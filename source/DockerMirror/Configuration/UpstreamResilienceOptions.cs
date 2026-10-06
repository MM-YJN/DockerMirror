using System.ComponentModel.DataAnnotations;

namespace DockerMirror.Configuration;

public sealed class UpstreamResilienceOptions
{
    /// <summary>
    /// Whether the resilience pipeline (retries, circuit-breaker, timeouts) is applied
    /// to upstream HTTP requests. Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// When <see langword="false"/>, requests to the upstream server are sent without
    /// any automatic retries, circuit-breaking, or Polly-managed timeouts. The
    /// <see cref="ConnectTimeout"/> for the underlying TCP connection is still enforced
    /// via the <see cref="System.Net.Http.SocketsHttpHandler"/> regardless of this setting.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum number of retry attempts on transient upstream errors. Defaults to <c>3</c>.
    /// </summary>
    /// <remarks>
    /// Retries use exponential back-off with jitter, starting from <see cref="BaseDelay"/>.
    /// Only transient errors (network failures, 5xx responses, timeouts) are retried.
    /// Must be in the range [0, 10].
    /// </remarks>
    [Range(0, 10)]
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>
    /// Base delay for exponential back-off between retry attempts. Defaults to 1 second.
    /// </summary>
    /// <remarks>
    /// The actual delay for each attempt is <c>BaseDelay × 2^attempt</c> with added
    /// random jitter. For example, with the default of 1 s the delays are approximately
    /// 1 s, 2 s, and 4 s before the 1st, 2nd, and 3rd retries respectively.
    /// </remarks>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Per-attempt timeout for buffered (non-streaming) upstream requests.
    /// Defaults to 10 seconds.
    /// </summary>
    /// <remarks>
    /// Applied as an inner timeout around each individual attempt of a buffered request
    /// (token fetch). If an attempt exceeds this limit it is cancelled and, if retries
    /// remain, the next attempt begins after the back-off delay. See also
    /// <see cref="TotalRequestTimeout"/>.
    /// </remarks>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Maximum total time allowed for a buffered upstream request including all retry
    /// attempts. Defaults to 30 seconds.
    /// </summary>
    /// <remarks>
    /// Applied as an outer timeout that caps the entire buffered request (all attempts
    /// combined). If this limit is reached the request fails immediately regardless of
    /// remaining retries.
    /// </remarks>
    public TimeSpan TotalRequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Timeout for receiving the response headers of a streaming upstream request
    /// (e.g. blob downloads). Defaults to 30 seconds.
    /// </summary>
    /// <remarks>
    /// For streaming responses the mirror begins forwarding data to the client as soon
    /// as headers arrive, so only the headers phase is covered by this timeout.
    /// The body transfer is bounded by the client's own read deadline rather than an
    /// additional Polly timeout.
    /// </remarks>
    public TimeSpan HeadersTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Fraction of requests within the <see cref="SamplingDuration"/> window that must
    /// fail before the circuit breaker trips. Defaults to <c>0.1</c> (10%).
    /// </summary>
    /// <remarks>
    /// Each circuit-breaker instance is shared by a group of upstream HTTP clients.
    /// Must be in the range [0.0, 1.0]. See also <see cref="MinimumThroughput"/>.
    /// </remarks>
    [Range(0.0, 1.0)]
    public double FailureRatio { get; set; } = 0.1;

    /// <summary>
    /// The sliding time window over which failures are counted for the circuit breaker.
    /// Defaults to 30 seconds.
    /// </summary>
    public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Minimum number of requests that must occur within the <see cref="SamplingDuration"/>
    /// window before the circuit breaker can trip. Defaults to <c>10</c>.
    /// </summary>
    /// <remarks>
    /// Prevents the circuit breaker from opening on a small number of failures during
    /// low-traffic periods. Must be ≥ 2.
    /// </remarks>
    [Range(2, int.MaxValue)]
    public int MinimumThroughput { get; set; } = 10;

    /// <summary>
    /// How long the circuit breaker stays open (rejecting requests) after it trips.
    /// Defaults to 15 seconds.
    /// </summary>
    /// <remarks>
    /// After this duration the circuit moves to half-open and allows a probe request
    /// through. If the probe succeeds the circuit closes; otherwise it opens again.
    /// </remarks>
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Maximum time allowed to establish a TCP connection to the upstream server.
    /// Defaults to 10 seconds.
    /// </summary>
    /// <remarks>
    /// Configured directly on the <see cref="System.Net.Http.SocketsHttpHandler"/> and
    /// applies regardless of whether the resilience pipeline is <see cref="Enabled"/>.
    /// </remarks>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
