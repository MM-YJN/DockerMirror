using System.Diagnostics;
using System.Net;

using DockerMirror.Configuration;
using DockerMirror.Diagnostics;

using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace DockerMirror.Registry;

public sealed partial class UpstreamRegistryClient(
    HttpClient http,
    IOptions<MirrorOptions> options,
    ILogger<UpstreamRegistryClient> logger,
    MirrorMetrics metrics)
{
    private static readonly HashSet<string> s_safeRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept",
        "Accept-Encoding",
        "Range",
    };

    public async Task ForwardAsync(string path, HttpRequest request, HttpResponse response, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);

        RegistryPath registryPath;
        try
        {
            registryPath = RegistryPath.Parse(path);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            await RegistryResponses.WriteBadPathAsync(response, ct).ConfigureAwait(false);
            return;
        }

        await ForwardParsedAsync(registryPath, request, response, ct).ConfigureAwait(false);
    }

    internal async Task ForwardParsedAsync(RegistryPath registryPath, HttpRequest request, HttpResponse response, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);

        string upstreamPath = BuildUpstreamPath(registryPath);
        await ForwardCoreAsync(upstreamPath, request, response, ct).ConfigureAwait(false);
    }

    public async Task ForwardRawAsync(string upstreamPath, HttpRequest request, HttpResponse response, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(upstreamPath);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);

        await ForwardCoreAsync(upstreamPath, request, response, ct).ConfigureAwait(false);
    }

    private async Task ForwardCoreAsync(string upstreamPath, HttpRequest request, HttpResponse response, CancellationToken ct)
    {
        using var upstreamRequest = new HttpRequestMessage(new HttpMethod(request.Method), upstreamPath + request.QueryString.Value);
        CopyRequestHeaders(request, upstreamRequest);

        using HttpResponseMessage upstreamResponse = await SendCoreAsync(upstreamRequest, ct).ConfigureAwait(false);

        response.StatusCode = (int)upstreamResponse.StatusCode;
        CopyResponseHeaders(upstreamResponse, response);

        if (HttpMethods.IsHead(request.Method) ||
            upstreamResponse.StatusCode is HttpStatusCode.Redirect or
                HttpStatusCode.MovedPermanently or
                HttpStatusCode.RedirectMethod or
                HttpStatusCode.TemporaryRedirect or
                HttpStatusCode.PermanentRedirect)
        {
            return;
        }

        await upstreamResponse.Content.CopyToAsync(response.Body, ct).ConfigureAwait(false);
    }

    // Standard manifest media types forwarded when the client sends no Accept header.
    internal static readonly string[] s_defaultManifestAcceptTypes =
    [
        "application/vnd.docker.distribution.manifest.v2+json",
        "application/vnd.docker.distribution.manifest.list.v2+json",
        "application/vnd.oci.image.manifest.v1+json",
        "application/vnd.oci.image.index.v1+json",
    ];

    internal async Task<HttpResponseMessage> SendUpstreamAsync(
        RegistryPath registryPath, HttpMethod method,
        IEnumerable<string>? acceptMediaTypes, CancellationToken ct,
        string? queryString = null,
        string? ifNoneMatch = null)
    {
        string upstreamPath = BuildUpstreamPath(registryPath);
        if (!string.IsNullOrEmpty(queryString))
        {
            upstreamPath += queryString;
        }

        using var upstreamRequest = new HttpRequestMessage(method, upstreamPath);
        upstreamRequest.Headers.Host = null;

        if (acceptMediaTypes is not null)
        {
            foreach (string mediaType in acceptMediaTypes)
            {
                upstreamRequest.Headers.TryAddWithoutValidation("Accept", mediaType);
            }
        }

        if (ifNoneMatch is not null)
        {
            upstreamRequest.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }

        return await SendCoreAsync(upstreamRequest, ct).ConfigureAwait(false);
    }

    // Send a raw upstream request used by ListResourceHandler for tags/list and _catalog.
    // Goes through the same authenticated SendCoreAsync path so token scoping is unchanged.
    internal async Task<HttpResponseMessage> SendRawAsync(
        string upstreamPath, HttpMethod method, string? ifNoneMatch, CancellationToken ct)
    {
        using var upstreamRequest = new HttpRequestMessage(method, upstreamPath);
        upstreamRequest.Headers.Host = null;

        if (ifNoneMatch is not null)
        {
            upstreamRequest.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }

        return await SendCoreAsync(upstreamRequest, ct).ConfigureAwait(false);
    }

    // Single chokepoint for every upstream HTTP call: times the request, records the
    // upstream request counter + duration histogram, and logs non-success / failures.
    private async Task<HttpResponseMessage> SendCoreAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string method = request.Method.Method;
        long startedTimestamp = Stopwatch.GetTimestamp();

        try
        {
            HttpResponseMessage response = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);

            double elapsedSeconds = Stopwatch.GetElapsedTime(startedTimestamp).TotalSeconds;
            int statusCode = (int)response.StatusCode;
            metrics.RecordUpstreamRequest(method, statusCode, elapsedSeconds);

            if (statusCode >= 400)
            {
                LogUpstreamNonSuccess(method, request.RequestUri?.AbsolutePath, statusCode);
            }

            return response;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            double elapsedSeconds = Stopwatch.GetElapsedTime(startedTimestamp).TotalSeconds;
            metrics.RecordUpstreamRequest(method, statusCode: 0, elapsedSeconds);
            LogUpstreamRequestFailed(ex, method, request.RequestUri?.AbsolutePath);
            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Upstream {Method} {Path} returned status {StatusCode}.")]
    private partial void LogUpstreamNonSuccess(string method, string? path, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Upstream {Method} {Path} request failed.")]
    private partial void LogUpstreamRequestFailed(Exception ex, string method, string? path);

    private string BuildUpstreamPath(RegistryPath registryPath)
    {
        string rewrittenName = registryPath.RewrittenName(options.Value.Upstream.DefaultNamespace);

        string path = $"v2/{rewrittenName}/{registryPath.ResourceType}";
        if (registryPath.Reference is not null)
        {
            path += $"/{registryPath.Reference}";
        }

        return path;
    }

    private static void CopyRequestHeaders(HttpRequest source, HttpRequestMessage destination)
    {
        foreach (KeyValuePair<string, StringValues> header in source.Headers)
        {
            if (s_safeRequestHeaders.Contains(header.Key))
            {
                destination.Headers.TryAddWithoutValidation(header.Key, [.. header.Value]);
            }
        }

        destination.Headers.Host = null;
    }

    internal static void CopyResponseHeaders(HttpResponseMessage source, HttpResponse destination)
    {
        foreach (KeyValuePair<string, IEnumerable<string>> header in source.Headers)
        {
            if (IsHopByHopHeader(header.Key))
            {
                continue;
            }

            destination.Headers[header.Key] = header.Value.ToArray();
        }

        foreach (KeyValuePair<string, IEnumerable<string>> header in source.Content.Headers)
        {
            if (IsHopByHopHeader(header.Key))
            {
                continue;
            }

            destination.Headers[header.Key] = header.Value.ToArray();
        }
    }

    private static bool IsHopByHopHeader(string name)
    {
        return name switch
        {
            "Transfer-Encoding" or
            "Connection" or
            "Keep-Alive" or
            "Proxy-Authenticate" or
            "Proxy-Authorization" or
            "TE" or
            "Trailers" or
            "Upgrade" => true,
            _ => false,
        };
    }
}
