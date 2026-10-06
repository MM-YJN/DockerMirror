using System.Net;

using DockerMirror.Configuration;
using DockerMirror.Diagnostics;
using DockerMirror.Registry;

using Microsoft.Extensions.Options;

namespace DockerMirror.Caching;

internal sealed partial class CacheWarmingService(
    UpstreamRegistryClient upstream,
    IContentStore store,
    KeyedAsyncLock keyedLock,
    CacheWarmingQueue queue,
    IHttpClientFactory httpClientFactory,
    IOptions<MirrorOptions> cacheOptions,
    ILogger<CacheWarmingService> logger,
    MirrorMetrics metrics,
    NegativeCacheGate negGate) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Cache services are always registered; gate actual work on the runtime config value
        // so this service is a true no-op when Mirror:Cache:Enabled=false.
        if (!cacheOptions.Value.Cache.Enabled)
        {
            return;
        }

        await foreach (WarmRequest request in queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await WarmAsync(request, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogWarmFailed(ex, request.Digest.Canonical);
                metrics.RecordWarm(succeeded: false);
            }
            finally
            {
                queue.MarkDone(request.Digest.Canonical);
            }
        }
    }

    private async Task WarmAsync(WarmRequest request, CancellationToken ct)
    {
        string cacheKey = DigestCacheKey.FromDigest(request.Digest);

        using IDisposable releaser = await keyedLock.LockAsync(cacheKey, ct).ConfigureAwait(false);

        if (await store.TryGetAsync(cacheKey, ct).ConfigureAwait(false) is not null)
        {
            return;
        }

        if (negGate.IsNegativeHit(request.RewrittenName, request.ResourceType, request.Digest.Canonical))
        {
            return;
        }

        var registryPath = new RegistryPath(request.RewrittenName, request.ResourceType, request.Digest.Canonical);

        HttpResponseMessage response;
        if (request.ResourceType == "blobs")
        {
            response = await FetchBlobAsync(registryPath, ct).ConfigureAwait(false);
        }
        else
        {
            response = await upstream.SendUpstreamAsync(
                registryPath, HttpMethod.Get,
                UpstreamRegistryClient.s_defaultManifestAcceptTypes, ct).ConfigureAwait(false);
        }

        try
        {
            if (response.StatusCode != HttpStatusCode.OK)
            {
                negGate.StoreIfNegative(response.StatusCode, request.RewrittenName, request.ResourceType, request.Digest.Canonical);
                return;
            }

            string contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";

            Stream upstreamStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            try
            {
                ICacheWriteHandle writeHandle = await store.BeginWriteAsync(cacheKey, request.Digest, ct).ConfigureAwait(false);
                await using (writeHandle.ConfigureAwait(false))
                {
                    writeHandle.SetMetadata(new CacheEntryMetadata(contentType));

                    await upstreamStream.CopyToAsync(writeHandle.Stream, ct).ConfigureAwait(false);
                    await writeHandle.Stream.FlushAsync(ct).ConfigureAwait(false);

                    bool committed = await writeHandle.CommitAsync(ct).ConfigureAwait(false);
                    if (committed)
                    {
                        LogWarmSucceeded(request.Digest.Canonical);
                        metrics.RecordWarm(succeeded: true);
                        metrics.RecordCacheWrite(
                            request.ResourceType, committed: true, response.Content.Headers.ContentLength ?? 0);
                    }
                }
            }
            finally
            {
                await upstreamStream.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            response.Dispose();
        }
    }

    private async Task<HttpResponseMessage> FetchBlobAsync(RegistryPath registryPath, CancellationToken ct)
    {
        // Blobs are opaque bytes; no Accept header required.
        HttpResponseMessage response = await upstream.SendUpstreamAsync(registryPath, HttpMethod.Get, null, ct).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Redirect or
            HttpStatusCode.MovedPermanently or
            HttpStatusCode.RedirectMethod or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect)
        {
            Uri? location = response.Headers.Location;
            response.Dispose();

            if (location is not null)
            {
                HttpClient redirectClient = httpClientFactory.CreateClient("upstream-redirect");
                HttpResponseMessage redirectResponse = await redirectClient.GetAsync(location, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                return redirectResponse;
            }

            var msg = new HttpResponseMessage(HttpStatusCode.NotFound);
            return msg;
        }

        return response;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Background warm failed for digest {Digest}.")]
    private partial void LogWarmFailed(Exception ex, string digest);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Background warm succeeded for digest {Digest}.")]
    private partial void LogWarmSucceeded(string digest);
}
