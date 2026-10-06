using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using DockerMirror.Configuration;
using DockerMirror.Diagnostics;
using DockerMirror.Json;

using Microsoft.Extensions.Options;

namespace DockerMirror.Registry;

public sealed partial class UpstreamTokenService(
    HttpClient http,
    IOptions<MirrorOptions> options,
    ILogger<UpstreamTokenService> logger,
    TimeProvider timeProvider,
    MirrorMetrics metrics)
{
    private readonly ConcurrentDictionary<string, CachedToken> _cache = new();

    public async ValueTask<string> GetTokenAsync(string scope, CancellationToken ct)
    {
        if (_cache.TryGetValue(scope, out CachedToken? cached) && cached.ExpiresAt > timeProvider.GetUtcNow())
        {
            return cached.Token;
        }

        return await FetchTokenAsync(scope, ct).ConfigureAwait(false);
    }

    public void Invalidate(string scope) => _cache.TryRemove(scope, out _);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Fetching token for scope: {Scope}")]
    private partial void LogFetchingToken(string scope);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Fetched token for scope {Scope}, expires in {Ttl}")]
    private partial void LogFetchedToken(string scope, TimeSpan ttl);

    private async Task<string> FetchTokenAsync(string scope, CancellationToken ct)
    {
        LogFetchingToken(scope);

        string url = $"?service={Uri.EscapeDataString(options.Value.Upstream.TokenService)}" +
                  $"&scope={Uri.EscapeDataString(scope)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (options.Value.Upstream.Auth is { } auth)
        {
            string credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{auth.Username}:{auth.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        using HttpResponseMessage response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        TokenResponse? tokenResponse = await JsonSerializer.DeserializeAsync(
            stream,
            RegistryJsonContext.Default.TokenResponse,
            ct).ConfigureAwait(false);

        if (tokenResponse is null)
        {
            throw new InvalidOperationException("Failed to deserialize token response.");
        }

        string? token = tokenResponse.Token ?? tokenResponse.AccessToken;
        if (string.IsNullOrEmpty(token))
        {
            throw new InvalidOperationException("Token response did not contain a token or access_token.");
        }

        int expiresIn = tokenResponse.ExpiresIn ?? 300;
        var ttl = TimeSpan.FromSeconds(Math.Max(60, expiresIn));
        if (options.Value.Upstream.TokenCacheTtl > TimeSpan.Zero && ttl > options.Value.Upstream.TokenCacheTtl)
        {
            ttl = options.Value.Upstream.TokenCacheTtl;
        }

        _cache[scope] = new CachedToken(token, timeProvider.GetUtcNow() + ttl);

        LogFetchedToken(scope, ttl);
        metrics.RecordTokenFetch();

        return token;
    }

    private sealed record CachedToken(string Token, DateTimeOffset ExpiresAt);
}
