using System.Buffers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

using DockerMirror.Caching.S3;
using DockerMirror.Configuration;

using Microsoft.Extensions.Options;

namespace DockerMirror.Caching;

internal sealed partial class S3ContentStore(S3.S3Client client, IOptions<MirrorOptions> options, ILogger<S3ContentStore> logger) : IContentStore, IRangeContentStore, ICacheMaintenance, ITagPointerStore, IStorageHealthProbe
{
    private readonly ILogger<S3ContentStore> _logger = logger;

    public async ValueTask<bool> ExistsAsync(string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        string s3Key = BuildS3Key(key);
        return await client.HeadExistsAsync(s3Key, ct).ConfigureAwait(false);
    }

    public async ValueTask<CachedContent?> TryGetAsync(string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        string s3Key = BuildS3Key(key);

        try
        {
            S3GetResult? result = await client.GetObjectAsync(s3Key, null, ct).ConfigureAwait(false);

            if (result is null)
            {
                return null;
            }

            var stream = new WrappedStream(result);
            return new CachedContent
            {
                Stream = stream,
                Length = result.ContentLength,
                ContentType = result.ContentType,
                Digest = result.Digest,
                StoredAtUtc = result.FetchedAt,
            };
        }
        catch (S3.S3Exception ex)
        {
            LogS3FetchFailed(ex, s3Key);
            return null;
        }
        catch (HttpRequestException ex)
        {
            LogS3HttpFetchFailed(ex, s3Key);
            return null;
        }
    }

    public async ValueTask<RangedContent?> TryGetRangeAsync(string key, string rangeHeader, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        string s3Key = BuildS3Key(key);

        try
        {
            S3GetResult? result = await client.GetObjectAsync(s3Key, rangeHeader, ct).ConfigureAwait(false);

            if (result is null)
            {
                return null;
            }

            return new RangedContent
            {
                Stream = result.Stream,
                StatusCode = (int)result.StatusCode,
                ContentLength = result.ContentLength,
                ContentType = result.ContentType,
                ContentRange = result.ContentRange,
                Digest = result.Digest,
                StoredAtUtc = result.FetchedAt,
                Owner = result,
            };
        }
        catch (S3.S3Exception ex)
        {
            LogS3FetchFailed(ex, s3Key);
            return null;
        }
        catch (HttpRequestException ex)
        {
            LogS3HttpFetchFailed(ex, s3Key);
            return null;
        }
    }

    public ValueTask<ICacheWriteHandle> BeginWriteAsync(string key, Digest expectedDigest, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        string s3Key = BuildS3Key(key);
        string tempPath = Path.GetTempFileName();
        var handle = new S3CacheWriteHandle(client, tempPath, s3Key, expectedDigest, _logger);
        return ValueTask.FromResult<ICacheWriteHandle>(handle);
    }

    public async IAsyncEnumerable<CacheEntryInfo> EnumerateAsync([EnumeratorCancellation] CancellationToken ct)
    {
        string prefix = options.Value.Cache.S3.KeyPrefix.TrimEnd('/');

        if (prefix.Length > 0)
        {
            prefix += "/";
        }

        IReadOnlyList<S3ListEntry> objects = await client.ListObjectsAsync(prefix, ct).ConfigureAwait(false);

        CacheEvictionOptions eviction = options.Value.Cache.Eviction;
        bool resolveCreatedAt = eviction.MaxAge is not null && eviction.Strategy == EvictionStrategy.Lru;

        foreach (S3ListEntry obj in objects)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(obj.Key) || obj.Key == prefix || obj.Key.EndsWith('/'))
            {
                continue;
            }

            string key = prefix.Length > 0 && obj.Key.StartsWith(prefix, StringComparison.Ordinal)
                ? obj.Key[prefix.Length..]
                : obj.Key;

            DateTimeOffset lastModified = obj.LastModified;
            DateTimeOffset createdAt;

            if (resolveCreatedAt)
            {
                try
                {
                    DateTimeOffset? fetchedAt = await client.HeadFetchedAtAsync(obj.Key, ct).ConfigureAwait(false);
                    createdAt = fetchedAt ?? lastModified;
                }
                catch (S3.S3Exception)
                {
                    createdAt = lastModified;
                }
                catch (HttpRequestException)
                {
                    createdAt = lastModified;
                }
            }
            else
            {
                createdAt = lastModified;
            }

            yield return new CacheEntryInfo(key, obj.Size, createdAt, lastModified);
        }
    }

    public ValueTask DeleteAsync(string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        string s3Key = BuildS3Key(key);
        return new ValueTask(client.DeleteObjectAsync(s3Key, ct));
    }

    public ValueTask TouchAsync(string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        string s3Key = BuildS3Key(key);
        return new ValueTask(client.TouchObjectAsync(s3Key, ct));
    }

    public async ValueTask<TagPointer?> TryGetTagAsync(string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        string s3Key = BuildS3Key(key);

        try
        {
            S3GetResult? result = await client.GetObjectAsync(s3Key, null, ct).ConfigureAwait(false);

            if (result is null)
            {
                return null;
            }

            try
            {
                if (!string.IsNullOrEmpty(result.Digest) &&
                    result.FetchedAt.HasValue &&
                    Digest.TryParse(result.Digest, out Digest digest))
                {
                    return new TagPointer(digest, result.FetchedAt.Value);
                }

                return null;
            }
            finally
            {
                await result.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (S3.S3Exception ex)
        {
            LogS3TagFetchFailed(ex, s3Key);
            return null;
        }
        catch (HttpRequestException ex)
        {
            LogS3TagHttpFetchFailed(ex, s3Key);
            return null;
        }
    }

    public async ValueTask SetTagAsync(string key, TagPointer pointer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        string s3Key = BuildS3Key(key);
        // PutObjectAsync disposes the stream via StreamContent — do not add a using here.
        var emptyStream = new MemoryStream();
        await client.PutObjectAsync(s3Key, emptyStream, "application/octet-stream",
            pointer.Digest.Canonical, S3.AwsSignatureV4.s_emptyPayloadHashHex, pointer.ResolvedAtUtc, ct).ConfigureAwait(false);
    }

    private string BuildS3Key(string key)
    {
        if (key.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Path traversal sequences ('..') are not allowed in cache keys.", nameof(key));
        }

        string prefix = options.Value.Cache.S3.KeyPrefix.TrimEnd('/');

        if (prefix.Length == 0)
        {
            return key.TrimStart('/');
        }

        return prefix + "/" + key.TrimStart('/');
    }

    ValueTask IStorageHealthProbe.CheckAsync(CancellationToken ct) => new(client.CheckAsync(ct));

    [LoggerMessage(Level = LogLevel.Warning, Message = "S3 fetch failed for {S3Key}; returning cache miss.")]
    private partial void LogS3FetchFailed(Exception ex, string s3Key);

    [LoggerMessage(Level = LogLevel.Warning, Message = "HTTP request to S3 failed for {S3Key}; returning cache miss.")]
    private partial void LogS3HttpFetchFailed(Exception ex, string s3Key);

    [LoggerMessage(Level = LogLevel.Warning, Message = "S3 tag pointer fetch failed for {S3Key}; returning cache miss.")]
    private partial void LogS3TagFetchFailed(Exception ex, string s3Key);

    [LoggerMessage(Level = LogLevel.Warning, Message = "HTTP request to S3 for tag pointer failed for {S3Key}; returning cache miss.")]
    private partial void LogS3TagHttpFetchFailed(Exception ex, string s3Key);

    private sealed class WrappedStream(S3.S3GetResult owner) : Stream
    {
        private readonly Stream _inner = owner.Stream;

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => _inner.ReadAsync(buffer, ct);

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            await owner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                owner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed partial class S3CacheWriteHandle(S3.S3Client client, string tempPath, string s3Key, Digest expectedDigest, ILogger<S3ContentStore> logger) : ICacheWriteHandle
    {
        private readonly ILogger<S3ContentStore> _logger = logger;
        private string _contentType = "application/octet-stream";
        private bool _committed;
        private bool _cleanedUp;

        public Stream Stream { get; } = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);

        public void SetMetadata(CacheEntryMetadata metadata) => _contentType = metadata.ContentType;

        public async ValueTask<bool> CommitAsync(CancellationToken ct)
        {
            await Stream.FlushAsync(ct).ConfigureAwait(false);
            Stream.Position = 0;

            (string? computedHex, string? payloadHashHex) = await ComputeHashesAsync(ct).ConfigureAwait(false);
            string computedDigest = $"{expectedDigest.Algorithm}:{computedHex}";

            if (!string.Equals(expectedDigest.Hex, computedHex, StringComparison.Ordinal))
            {
                LogDigestMismatch(expectedDigest.Canonical, computedDigest, tempPath);
                await CleanupTempFileAsync().ConfigureAwait(false);
                _cleanedUp = true;
                return false;
            }

            await client.PutObjectAsync(s3Key, Stream, _contentType, computedDigest, payloadHashHex, null, ct).ConfigureAwait(false);

            // StreamContent inside PutObjectAsync disposed the temp FileStream.
            // Only delete the file — do not call Stream.DisposeAsync() again.
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception ex)
            {
                LogDeleteFailed(ex, tempPath);
            }

            _committed = true;
            return true;
        }

        public async ValueTask DisposeAsync()
        {
            if (_committed || _cleanedUp)
            {
                return;
            }

            await CleanupTempFileAsync().ConfigureAwait(false);
        }

        private async ValueTask CleanupTempFileAsync()
        {
            await Stream.DisposeAsync().ConfigureAwait(false);

            try
            {
                File.Delete(tempPath);
            }
            catch (Exception ex)
            {
                LogDeleteFailed(ex, tempPath);
            }
        }

        private async Task<(string DigestHex, string PayloadHashHex)> ComputeHashesAsync(CancellationToken ct)
        {
            using var contentHash = IncrementalHash.CreateHash(expectedDigest.AlgorithmName);
            using var payloadHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            const int BufferSize = 81920;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                int read;
                while ((read = await Stream.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
                {
                    contentHash.AppendData(buffer, 0, read);
                    payloadHash.AppendData(buffer, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            Stream.Position = 0;
            return (Convert.ToHexStringLower(contentHash.GetHashAndReset()),
                    Convert.ToHexStringLower(payloadHash.GetHashAndReset()));
        }

        [LoggerMessage(Level = LogLevel.Warning, Message = "Digest mismatch for {ExpectedDigest}: computed {ComputedDigest}. Temp file {Path} deleted, not cached.")]
        private partial void LogDigestMismatch(string expectedDigest, string computedDigest, string path);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete temporary cache file {Path}.")]
        private partial void LogDeleteFailed(Exception ex, string path);
    }
}
