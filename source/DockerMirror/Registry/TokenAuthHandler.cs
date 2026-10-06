using System.Net;
using System.Net.Http.Headers;

namespace DockerMirror.Registry;

public sealed partial class TokenAuthHandler(
    UpstreamTokenService tokenService,
    ILogger<TokenAuthHandler> logger) : DelegatingHandler
{
    private readonly UpstreamTokenService _tokenService = tokenService;
    private readonly ILogger<TokenAuthHandler> _logger = logger;

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Received 401 for scope {Scope}, invalidating cached token and retrying")]
    private static partial void LogUnauthorized(ILogger logger, string scope);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Uri uri = request.RequestUri ?? throw new ArgumentException("Request must have a URI.", nameof(request));
        string scope = ComputeScope(uri);

        string token = await _tokenService.GetTokenAsync(scope, cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        LogUnauthorized(_logger, scope);

        HttpHeaderValueCollection<AuthenticationHeaderValue> wwwAuth = response.Headers.WwwAuthenticate;
        response.Dispose();

        string retryScope = ParseScopeFromWwwAuthenticate(wwwAuth) ?? scope;

        _tokenService.Invalidate(retryScope);
        string freshToken = await _tokenService.GetTokenAsync(retryScope, cancellationToken).ConfigureAwait(false);

        using var retryRequest = new HttpRequestMessage(request.Method, request.RequestUri);

        foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
        {
            retryRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", freshToken);

        return await base.SendAsync(retryRequest, cancellationToken).ConfigureAwait(false);
    }

    private static string ComputeScope(Uri uri)
    {
        string path = uri.AbsolutePath;
        if (!path.StartsWith("/v2/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unexpected registry URI path: {path}");
        }

        if (path.Equals("/v2/_catalog", StringComparison.Ordinal))
        {
            return "registry:catalog:*";
        }

        var registryPath = RegistryPath.Parse(path.AsSpan(4));
        return registryPath.Scope;
    }

    private static string? ParseScopeFromWwwAuthenticate(HttpHeaderValueCollection<AuthenticationHeaderValue> wwwAuth)
    {
        foreach (AuthenticationHeaderValue auth in wwwAuth)
        {
            if (!string.Equals(auth.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.IsNullOrEmpty(auth.Parameter))
            {
                continue;
            }

            string? scope = ParseKeyValueParameter(auth.Parameter, "scope");
            if (scope is not null)
            {
                return scope;
            }
        }

        return null;
    }

    private static string? ParseKeyValueParameter(string parameter, string targetKey)
    {
        ReadOnlySpan<char> span = parameter.AsSpan();

        while (span.Length > 0)
        {
            int comma = span.IndexOf(',');

            ReadOnlySpan<char> pair = comma < 0 ? span : span[..comma];
            span = comma < 0 ? [] : span[(comma + 1)..];

            int eq = pair.IndexOf('=');
            if (eq < 0)
            {
                continue;
            }

            ReadOnlySpan<char> key = pair[..eq].Trim();
            if (!key.Equals(targetKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ReadOnlySpan<char> value = pair[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                return value[1..^1].ToString();
            }

            return value.ToString();
        }

        return null;
    }
}
