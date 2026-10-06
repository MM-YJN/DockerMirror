using System.Globalization;
using System.Net;
using System.Xml;

using DockerMirror.Configuration;

using Microsoft.Extensions.Options;

namespace DockerMirror.Caching.S3;

internal sealed partial class S3Client(HttpClient http, IOptions<MirrorOptions> options, ILogger<S3Client> logger) : IDisposable
{
    public async Task<S3GetResult?> GetObjectAsync(string key, string? range, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        S3CacheOptions s3 = options.Value.Cache.S3;
        string baseUrl = GetBaseUrl(s3);
        var uri = new Uri($"{baseUrl}/{EscapeKey(key)}");
        string host = GetHost(s3);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Host", host);

        if (range is not null)
        {
            request.Headers.TryAddWithoutValidation("Range", range);
        }

        var signer = new AwsSignatureV4(s3.AccessKey, s3.SecretKey, s3.Region);
        signer.Sign(request, AwsSignatureV4.s_emptyPayloadHashHex);

        HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            response.Dispose();
            LogS3NotFound(key);
            return null;
        }

        if (response.StatusCode is HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            string? contentRange = GetContentRangeHeader(response);
            string contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            string digest = ExtractMetaHeader(response, "x-amz-meta-digest") ?? string.Empty;

            if (contentRange is null)
            {
                S3CacheOptions s3Opts = options.Value.Cache.S3;
                long? size = await HeadObjectSizeAsync(key, s3Opts, ct).ConfigureAwait(false);
                if (size.HasValue)
                {
                    contentRange = $"bytes */{size.Value}";
                }
            }

            return new S3GetResult(response, Stream.Null, 0, contentType, digest, null, HttpStatusCode.RequestedRangeNotSatisfiable, contentRange);
        }

        try
        {
            response.EnsureSuccessStatusCode();

            LogS3GetSuccess(key, response.Content.Headers.ContentLength ?? -1L);

            long contentLength = response.Content.Headers.ContentLength ?? -1L;
            string contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            string? digest = ExtractMetaHeader(response, "x-amz-meta-digest");
            DateTimeOffset? fetchedAt = ParseFetchedAtHeader(response);
            string? contentRange = response.Content.Headers.ContentRange?.ToString();

            Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return new S3GetResult(response, stream, contentLength, contentType, digest ?? string.Empty, fetchedAt, response.StatusCode, contentRange);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public async Task PutObjectAsync(string key, Stream content, string contentType, string digest, string? payloadHashHex, DateTimeOffset? fetchedAt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        S3CacheOptions s3 = options.Value.Cache.S3;
        string baseUrl = GetBaseUrl(s3);
        var uri = new Uri($"{baseUrl}/{EscapeKey(key)}");
        string host = GetHost(s3);

        string payloadHash = payloadHashHex ?? AwsSignatureV4.ComputePayloadHashHex(content);

        using var request = new HttpRequestMessage(HttpMethod.Put, uri);
        request.Headers.TryAddWithoutValidation("Host", host);

        request.Content = new StreamContent(content);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);

        request.Headers.TryAddWithoutValidation("x-amz-meta-digest", digest);
        request.Headers.TryAddWithoutValidation("x-amz-meta-fetched-at", (fetchedAt ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));

        var signer = new AwsSignatureV4(s3.AccessKey, s3.SecretKey, s3.Region);
        signer.Sign(request, payloadHash);

        using HttpResponseMessage response = await http.SendAsync(request, ct).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
        {
            LogS3PutSuccess(key);
            return;
        }

        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new S3Exception(response.StatusCode, body);
    }

    public async Task CreateBucketAsync(CancellationToken ct)
    {
        S3CacheOptions s3 = options.Value.Cache.S3;
        string host = GetHost(s3);
        string baseUrl = GetBaseUrl(s3);

        using var request = new HttpRequestMessage(HttpMethod.Put, baseUrl + "/");
        request.Headers.TryAddWithoutValidation("Host", host);

        var signer = new AwsSignatureV4(s3.AccessKey, s3.SecretKey, s3.Region);
        signer.Sign(request, AwsSignatureV4.s_emptyPayloadHashHex);

        using HttpResponseMessage response = await http.SendAsync(request, ct).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict)
        {
            LogS3CreateBucketSuccess();
            return;
        }

        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new S3Exception(response.StatusCode, body);
    }

    public async Task CheckAsync(CancellationToken ct)
    {
        S3CacheOptions s3 = options.Value.Cache.S3;
        string baseUrl = GetBaseUrl(s3);
        string host = GetHost(s3);

        var uri = new Uri(baseUrl + "?max-keys=1");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Host", host);

        var signer = new AwsSignatureV4(s3.AccessKey, s3.SecretKey, s3.Region);
        signer.Sign(request, AwsSignatureV4.s_emptyPayloadHashHex);

        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new S3Exception(response.StatusCode, body);
        }
    }

    public void Dispose() => http.Dispose();

    public async Task<IReadOnlyList<S3ListEntry>> ListObjectsAsync(string prefix, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        S3CacheOptions s3 = options.Value.Cache.S3;
        string baseUrl = GetBaseUrl(s3);
        string host = GetHost(s3);
        var results = new List<S3ListEntry>();
        string? continuationToken = null;

        do
        {
            string queryString = "?list-type=2&prefix=" + Uri.EscapeDataString(prefix);

            if (continuationToken is not null)
            {
                queryString += "&continuation-token=" + Uri.EscapeDataString(continuationToken);
            }

            var uri = new Uri(baseUrl + queryString);

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("Host", host);

            var signer = new AwsSignatureV4(s3.AccessKey, s3.SecretKey, s3.Region);
            signer.Sign(request, AwsSignatureV4.s_emptyPayloadHashHex);

            using HttpResponseMessage response = await http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            string xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            (IReadOnlyList<S3ListEntry>? entries, continuationToken) = ParseListObjectsResult(xml);
            results.AddRange(entries);
        }
        while (continuationToken is not null);

        LogS3ListSuccess(prefix, results.Count);
        return results;
    }

    public async Task DeleteObjectAsync(string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        S3CacheOptions s3 = options.Value.Cache.S3;
        string baseUrl = GetBaseUrl(s3);
        var uri = new Uri($"{baseUrl}/{EscapeKey(key)}");
        string host = GetHost(s3);

        using var request = new HttpRequestMessage(HttpMethod.Delete, uri);
        request.Headers.TryAddWithoutValidation("Host", host);

        var signer = new AwsSignatureV4(s3.AccessKey, s3.SecretKey, s3.Region);
        signer.Sign(request, AwsSignatureV4.s_emptyPayloadHashHex);

        using HttpResponseMessage response = await http.SendAsync(request, ct).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound)
        {
            LogS3DeleteSuccess(key);
            return;
        }

        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new S3Exception(response.StatusCode, body);
    }

    public async Task TouchObjectAsync(string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        S3CacheOptions s3 = options.Value.Cache.S3;

        // Retrieve the current object metadata so we can preserve it through the copy.
        // AWS S3 rejects a self-copy that uses x-amz-metadata-directive: COPY because the
        // source and destination are identical and nothing changes.  REPLACE is required, but
        // REPLACE blanks every metadata header we do not explicitly re-supply.  We HEAD the
        // object first to recover the content-type and digest, then re-supply them so the
        // cache entry remains valid after the touch.
        S3ObjectMeta? meta = await HeadObjectAsync(key, s3, ct).ConfigureAwait(false);
        if (meta is null)
        {
            // Object no longer exists; nothing to touch.
            return;
        }

        string baseUrl = GetBaseUrl(s3);
        var uri = new Uri($"{baseUrl}/{EscapeKey(key)}");
        string host = GetHost(s3);
        string source = $"/{s3.Bucket}/{EscapeKey(key)}";

        using var request = new HttpRequestMessage(HttpMethod.Put, uri);
        request.Headers.TryAddWithoutValidation("Host", host);
        request.Headers.TryAddWithoutValidation("x-amz-copy-source", source);
        request.Headers.TryAddWithoutValidation("x-amz-metadata-directive", "REPLACE");

        if (!string.IsNullOrEmpty(meta.Digest))
        {
            request.Headers.TryAddWithoutValidation("x-amz-meta-digest", meta.Digest);
        }

        if (meta.FetchedAt.HasValue)
        {
            request.Headers.TryAddWithoutValidation("x-amz-meta-fetched-at",
                meta.FetchedAt.Value.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        }

        // REPLACE requires a Content-Type on the copy destination; re-supply the original value.
        request.Content = new StringContent(string.Empty);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(meta.ContentType);

        var signer = new AwsSignatureV4(s3.AccessKey, s3.SecretKey, s3.Region);
        signer.Sign(request, AwsSignatureV4.s_emptyPayloadHashHex);

        using HttpResponseMessage response = await http.SendAsync(request, ct).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent)
        {
            LogS3TouchSuccess(key);
            return;
        }

        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new S3Exception(response.StatusCode, body);
    }

    // Returns the content-type, digest, and optional fetched-at timestamp of an existing
    // object, or null if the object does not exist.  Used by TouchObjectAsync to preserve
    // metadata through a REPLACE copy.
    private async Task<S3ObjectMeta?> HeadObjectAsync(string key, S3CacheOptions s3, CancellationToken ct)
    {
        string baseUrl = GetBaseUrl(s3);
        var uri = new Uri($"{baseUrl}/{EscapeKey(key)}");
        string host = GetHost(s3);

        using var request = new HttpRequestMessage(HttpMethod.Head, uri);
        request.Headers.TryAddWithoutValidation("Host", host);

        var signer = new AwsSignatureV4(s3.AccessKey, s3.SecretKey, s3.Region);
        signer.Sign(request, AwsSignatureV4.s_emptyPayloadHashHex);

        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        string contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        string digest = ExtractMetaHeader(response, "x-amz-meta-digest") ?? string.Empty;
        DateTimeOffset? fetchedAt = ParseFetchedAtHeader(response);
        return new S3ObjectMeta(contentType, digest, fetchedAt);
    }

    // Returns the fetched-at metadata timestamp for an object, or null if the object
    // does not exist or lacks the header.  Used by EnumerateAsync to recover CreatedAtUtc
    // when the object has been touched under LRU.
    public async Task<DateTimeOffset?> HeadFetchedAtAsync(string key, CancellationToken ct)
    {
        S3CacheOptions s3 = options.Value.Cache.S3;
        S3ObjectMeta? meta = await HeadObjectAsync(key, s3, ct).ConfigureAwait(false);
        return meta?.FetchedAt;
    }

    // Returns true when the object exists (HEAD returns a non-404 status).
    // Catches S3 errors and returns false so callers can treat any S3 error the
    // same as a missing object — the caller just wants to know whether the body
    // is cached. Cancellation is always propagated.
    public async ValueTask<bool> HeadExistsAsync(string key, CancellationToken ct)
    {
        try
        {
            S3CacheOptions s3 = options.Value.Cache.S3;
            S3ObjectMeta? meta = await HeadObjectAsync(key, s3, ct).ConfigureAwait(false);
            return meta is not null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private sealed record S3ObjectMeta(string ContentType, string Digest, DateTimeOffset? FetchedAt);

    private static (IReadOnlyList<S3ListEntry> Entries, string? NextContinuationToken) ParseListObjectsResult(string xml)
    {
        var entries = new List<S3ListEntry>();
        string? nextToken = null;

        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { IgnoreWhitespace = true, Async = false });

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.Name == "Contents")
            {
                S3ListEntry entry = ParseListEntry(reader);
                entries.Add(entry);
            }
            else if (reader.Name == "NextContinuationToken")
            {
                reader.Read();
                nextToken = reader.Value;
            }
        }

        return (entries, nextToken);
    }

    private static S3ListEntry ParseListEntry(XmlReader reader)
    {
        string? key = null;
        long size = 0;
        DateTimeOffset lastModified = default;

        int depth = reader.Depth;
        while (reader.Read() && (reader.Depth > depth || reader.NodeType != XmlNodeType.EndElement))
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (reader.Name)
            {
                case "Key":
                    reader.Read();
                    key = reader.Value;
                    break;

                case "Size":
                    reader.Read();
                    _ = long.TryParse(reader.Value, out size);
                    break;

                case "LastModified":
                    reader.Read();
                    _ = DateTimeOffset.TryParse(reader.Value, out lastModified);
                    break;
            }
        }

        return new S3ListEntry(key ?? "", size, lastModified);
    }

    private static string GetBaseUrl(S3CacheOptions s3)
    {
        if (s3.UsePathStyle)
        {
            string endpoint = !string.IsNullOrEmpty(s3.ServiceUrl)
                ? s3.ServiceUrl.TrimEnd('/')
                : $"https://s3.{s3.Region}.amazonaws.com";
            return $"{endpoint}/{s3.Bucket}";
        }

        if (!string.IsNullOrEmpty(s3.ServiceUrl))
        {
            string host = new Uri(s3.ServiceUrl).Authority;
            return $"https://{s3.Bucket}.{host}";
        }

        return $"https://{s3.Bucket}.s3.{s3.Region}.amazonaws.com";
    }

    private static string GetHost(S3CacheOptions s3)
    {
        if (s3.UsePathStyle)
        {
            string endpoint = !string.IsNullOrEmpty(s3.ServiceUrl)
                ? s3.ServiceUrl
                : $"https://s3.{s3.Region}.amazonaws.com";
            return new Uri(endpoint).Authority;
        }

        if (!string.IsNullOrEmpty(s3.ServiceUrl))
        {
            string host = new Uri(s3.ServiceUrl).Authority;
            return $"{s3.Bucket}.{host}";
        }

        return $"{s3.Bucket}.s3.{s3.Region}.amazonaws.com";
    }

    private static string EscapeKey(string key) => AwsSignatureV4.EncodePathSegment(key);

    private static string? GetContentRangeHeader(HttpResponseMessage response)
    {
        string? cr = response.Content.Headers.ContentRange?.ToString();
        if (cr is not null)
        {
            return cr;
        }

        if (response.Content.Headers.TryGetValues("Content-Range", out IEnumerable<string>? crValues))
        {
            return crValues.FirstOrDefault();
        }

        if (response.Headers.TryGetValues("Content-Range", out IEnumerable<string>? crValues2))
        {
            return crValues2.FirstOrDefault();
        }

        return null;
    }

    private async ValueTask<long?> HeadObjectSizeAsync(string key, S3CacheOptions s3, CancellationToken ct)
    {
        string baseUrl = GetBaseUrl(s3);
        var uri = new Uri($"{baseUrl}/{EscapeKey(key)}");
        string host = GetHost(s3);

        using var request = new HttpRequestMessage(HttpMethod.Head, uri);
        request.Headers.TryAddWithoutValidation("Host", host);

        var signer = new AwsSignatureV4(s3.AccessKey, s3.SecretKey, s3.Region);
        signer.Sign(request, AwsSignatureV4.s_emptyPayloadHashHex);

        using HttpResponseMessage headResponse = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (headResponse.StatusCode is HttpStatusCode.NotFound)
        {
            return null;
        }

        headResponse.EnsureSuccessStatusCode();
        return headResponse.Content.Headers.ContentLength;
    }

    private static string? ExtractMetaHeader(HttpResponseMessage response, string name)
    {
        foreach ((string? key, IEnumerable<string>? values) in response.Headers)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return values.FirstOrDefault();
            }
        }

        return null;
    }

    private static DateTimeOffset? ParseFetchedAtHeader(HttpResponseMessage response)
    {
        string? fetchedAtStr = ExtractMetaHeader(response, "x-amz-meta-fetched-at");

        if (fetchedAtStr is not null && long.TryParse(fetchedAtStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out long fetchedAtMs))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(fetchedAtMs);
        }

        return null;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "S3 get {Key} returned {Length} bytes")]
    private partial void LogS3GetSuccess(string key, long length);

    [LoggerMessage(Level = LogLevel.Debug, Message = "S3 get {Key}: not found")]
    private partial void LogS3NotFound(string key);

    [LoggerMessage(Level = LogLevel.Debug, Message = "S3 put {Key} succeeded")]
    private partial void LogS3PutSuccess(string key);

    [LoggerMessage(Level = LogLevel.Debug, Message = "S3 delete {Key} succeeded")]
    private partial void LogS3DeleteSuccess(string key);

    [LoggerMessage(Level = LogLevel.Debug, Message = "S3 touch {Key} succeeded")]
    private partial void LogS3TouchSuccess(string key);

    [LoggerMessage(Level = LogLevel.Debug, Message = "S3 list {Prefix} returned {Count} objects")]
    private partial void LogS3ListSuccess(string prefix, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "S3 bucket created or already exists")]
    private partial void LogS3CreateBucketSuccess();
}
