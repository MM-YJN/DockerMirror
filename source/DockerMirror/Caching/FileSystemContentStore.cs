using System.Globalization;
using System.Runtime.CompilerServices;

using DockerMirror.Configuration;

using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace DockerMirror.Caching;

internal sealed partial class FileSystemContentStore(IOptions<MirrorOptions> options, ILogger<FileSystemContentStore> logger) : IContentStore, IRangeContentStore, ICacheMaintenance, ITagPointerStore, IStorageHealthProbe
{
    private readonly string _root = Path.GetFullPath(options.Value.Cache.FileSystem.Directory);
    private readonly ILogger<FileSystemContentStore> _logger = logger;

    public ValueTask<bool> ExistsAsync(string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        string contentPath = KeyToPath(key);
        return ValueTask.FromResult(File.Exists(contentPath));
    }

    public async ValueTask<CachedContent?> TryGetAsync(string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        string contentPath = KeyToPath(key);
        string metaPath = contentPath + ".meta";

        FileStream stream;
        try
        {
            stream = new FileStream(contentPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException) when (!File.Exists(contentPath))
        {
            return null;
        }

        if (File.Exists(metaPath))
        {
            try
            {
                MetaInfo meta = await ReadMetaAsync(metaPath, ct).ConfigureAwait(false);
                if (meta.Length >= 0)
                {
                    return new CachedContent
                    {
                        Stream = stream,
                        Length = meta.Length,
                        ContentType = meta.ContentType,
                        Digest = meta.Digest,
                        StoredAtUtc = meta.FetchedAt,
                    };
                }
            }
            catch (Exception ex)
            {
                LogMetaReadFallback(ex, metaPath);
            }
        }

        var fileInfo = new FileInfo(contentPath);
        return new CachedContent
        {
            Stream = stream,
            Length = fileInfo.Length,
            ContentType = "application/octet-stream",
            Digest = string.Empty,
        };
    }

    public async ValueTask<RangedContent?> TryGetRangeAsync(string key, string rangeHeader, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(rangeHeader);

        string contentPath = KeyToPath(key);
        string metaPath = contentPath + ".meta";

        FileStream fs;
        try
        {
            fs = new FileStream(contentPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException) when (!File.Exists(contentPath))
        {
            return null;
        }

        long length = fs.Length;
        string contentType = "application/octet-stream";
        string digest = string.Empty;
        DateTimeOffset? storedAtUtc = null;

        if (File.Exists(metaPath))
        {
            try
            {
                MetaInfo meta = await ReadMetaAsync(metaPath, ct).ConfigureAwait(false);
                if (meta.Length >= 0)
                {
                    contentType = meta.ContentType;
                    digest = meta.Digest;
                    storedAtUtc = meta.FetchedAt;
                }
            }
            catch (Exception ex)
            {
                LogMetaReadFallback(ex, metaPath);
            }
        }

        if (!RangeHeaderValue.TryParse(rangeHeader, out RangeHeaderValue? parsed) ||
            !string.Equals(parsed.Unit.Value, "bytes", StringComparison.OrdinalIgnoreCase) ||
            parsed.Ranges.Count != 1)
        {
            return new RangedContent
            {
                Stream = fs,
                StatusCode = 200,
                ContentLength = length,
                ContentType = contentType,
                ContentRange = null,
                Digest = digest,
                StoredAtUtc = storedAtUtc,
                Owner = fs,
            };
        }

        RangeItemHeaderValue range = parsed.Ranges.First();

        async ValueTask<RangedContent> Make416Async()
        {
            await fs.DisposeAsync().ConfigureAwait(false);
            return new RangedContent
            {
                Stream = Stream.Null,
                StatusCode = 416,
                ContentLength = 0,
                ContentType = contentType,
                ContentRange = $"bytes */{length}",
                Digest = digest,
                StoredAtUtc = storedAtUtc,
                Owner = Stream.Null,
            };
        }

        if (length == 0)
        {
            return await Make416Async().ConfigureAwait(false);
        }

        long start;
        long end;

        if (range.From.HasValue)
        {
            start = range.From.Value;
            if (start >= length)
            {
                return await Make416Async().ConfigureAwait(false);
            }

            end = Math.Min(range.To ?? length - 1, length - 1);
        }
        else
        {
            long suffix = range.To ?? 0;
            if (suffix == 0)
            {
                return await Make416Async().ConfigureAwait(false);
            }

            start = length - Math.Min(suffix, length);
            end = length - 1;
        }

        if (end < start)
        {
            return await Make416Async().ConfigureAwait(false);
        }

        long count = end - start + 1;
        fs.Seek(start, SeekOrigin.Begin);
        var bounded = new BoundedReadStream(fs, count);

        return new RangedContent
        {
            Stream = bounded,
            StatusCode = 206,
            ContentLength = count,
            ContentType = contentType,
            ContentRange = $"bytes {start}-{end}/{length}",
            Digest = digest,
            StoredAtUtc = storedAtUtc,
            Owner = bounded,
        };
    }

    public ValueTask<ICacheWriteHandle> BeginWriteAsync(string key, Digest expectedDigest, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        string contentPath = KeyToPath(key);
        string? dir = Path.GetDirectoryName(contentPath);

        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        string tempPath = contentPath + ".tmp." + Guid.NewGuid().ToString("N");
        var handle = new FileSystemCacheWriteHandle(tempPath, contentPath, expectedDigest, _logger);
        return ValueTask.FromResult<ICacheWriteHandle>(handle);
    }

    public async IAsyncEnumerable<CacheEntryInfo> EnumerateAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (!Directory.Exists(_root))
        {
            yield break;
        }

        CacheEvictionOptions eviction = options.Value.Cache.Eviction;
        bool readFetchedAt = eviction.MaxAge is not null || eviction.Strategy != EvictionStrategy.Lru;

        foreach (string filePath in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            ReadOnlySpan<char> fileName = Path.GetFileName(filePath.AsSpan());

            if (fileName.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ||
                fileName.Contains(".tmp.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string key = PathToKey(filePath);
            var fileInfo = new FileInfo(filePath);
            DateTime lastModified = fileInfo.LastWriteTimeUtc;
            DateTimeOffset createdAt = lastModified;

            if (readFetchedAt)
            {
                try
                {
                    createdAt = await TryReadFetchedAtAsync(filePath + ".meta", ct).ConfigureAwait(false) ?? lastModified;
                }
                catch (Exception ex)
                {
                    LogSweepMetaReadFailed(ex, filePath);
                }
            }

            yield return new CacheEntryInfo(key, fileInfo.Length, createdAt, lastModified);
        }
    }

    public ValueTask DeleteAsync(string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        string contentPath = KeyToPath(key);
        string metaPath = contentPath + ".meta";

        try
        {
            if (File.Exists(contentPath))
            {
                File.Delete(contentPath);
            }

            if (File.Exists(metaPath))
            {
                File.Delete(metaPath);
            }
        }
        catch (Exception ex)
        {
            LogDeleteFailed(ex, key);
            return ValueTask.CompletedTask;
        }

        PruneEmptyDirectories(contentPath);
        return ValueTask.CompletedTask;
    }

    public ValueTask TouchAsync(string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        string contentPath = KeyToPath(key);

        try
        {
            if (File.Exists(contentPath))
            {
                File.SetLastWriteTimeUtc(contentPath, DateTime.UtcNow);
            }
        }
        catch (Exception ex)
        {
            LogTouchFailed(ex, key);
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<TagPointer?> TryGetTagAsync(string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        string path = KeyToPath(key);

        try
        {
            string[] lines = await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false);
            Digest digest = default;
            DateTimeOffset resolvedAt = default;
            bool hasDigest = false;
            bool hasResolved = false;

            foreach (string line in lines)
            {
                int eq = line.IndexOf('=');
                if (eq < 0)
                {
                    continue;
                }

                string field = line[..eq];
                string value = line[(eq + 1)..];

                switch (field)
                {
                    case "digest":
                        if (Digest.TryParse(value, out Digest d))
                        {
                            digest = d;
                            hasDigest = true;
                        }

                        break;

                    case "resolved-at":
                        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ms))
                        {
                            resolvedAt = DateTimeOffset.FromUnixTimeMilliseconds(ms);
                            hasResolved = true;
                        }

                        break;
                }
            }

            if (hasDigest && hasResolved)
            {
                return new TagPointer(digest, resolvedAt);
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException) when (!File.Exists(path))
        {
        }
        catch (Exception ex)
        {
            LogTagPointerReadFailed(ex, key);
        }

        return null;
    }

    public async ValueTask SetTagAsync(string key, TagPointer pointer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        string targetPath = KeyToPath(key);
        string? dir = Path.GetDirectoryName(targetPath);

        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        string tempPath = targetPath + ".tmp." + Guid.NewGuid().ToString("N");
        string[] lines = new[]
        {
            $"digest={pointer.Digest.Canonical}",
            $"resolved-at={pointer.ResolvedAtUtc.ToUnixTimeMilliseconds()}",
        };

        try
        {
            await File.WriteAllLinesAsync(tempPath, lines, ct).ConfigureAwait(false);
            await Task.Run(() => File.Move(tempPath, targetPath, overwrite: true), ct).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // Best-effort cleanup; the file will be skipped by eviction enumeration.
            }

            throw;
        }
    }

    private string PathToKey(string filePath)
    {
        string relativePath = Path.GetRelativePath(_root, filePath);
        return relativePath.Replace(Path.DirectorySeparatorChar, '/');
    }

    private void PruneEmptyDirectories(string contentPath)
    {
        string? dir = Path.GetDirectoryName(contentPath);

        while (dir is not null && dir.Length > _root.Length)
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                    dir = Path.GetDirectoryName(dir);
                }
                else
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                LogPruneDirectoryFailed(ex, dir);
                break;
            }
        }
    }

    private string KeyToPath(string key)
    {
        string sanitizedKey = key.Replace('/', Path.DirectorySeparatorChar);

        foreach (string segment in sanitizedKey.Split(Path.DirectorySeparatorChar))
        {
            if (segment is "." or "..")
            {
                throw new ArgumentException("Key must not contain path-traversal segments.", nameof(key));
            }
        }

        string joined = Path.Join(_root, sanitizedKey);
        string fullPath = Path.GetFullPath(joined);
        string canonicalRoot = Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(canonicalRoot, StringComparison.Ordinal))
        {
            throw new ArgumentException("Key resolves outside the cache root.", nameof(key));
        }

        return fullPath;
    }

    private static async Task<MetaInfo> ReadMetaAsync(string metaPath, CancellationToken ct)
    {
        string[] lines = await File.ReadAllLinesAsync(metaPath, ct).ConfigureAwait(false);
        var meta = new MetaInfo();

        foreach (string line in lines)
        {
            int eq = line.IndexOf('=');

            if (eq < 0)
            {
                continue;
            }

            string field = line[..eq];
            string value = line[(eq + 1)..];

            switch (field)
            {
                case "length":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long length))
                    {
                        meta.Length = length;
                    }

                    break;

                case "content-type":
                    meta.ContentType = value;
                    break;

                case "digest":
                    meta.Digest = value;
                    break;

                case "fetched-at":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long fetchedAtMs))
                    {
                        meta.FetchedAt = DateTimeOffset.FromUnixTimeMilliseconds(fetchedAtMs);
                    }

                    break;
            }
        }

        return meta;
    }

    private static async Task<DateTimeOffset?> TryReadFetchedAtAsync(string metaPath, CancellationToken ct)
    {
        try
        {
            string[] lines = await File.ReadAllLinesAsync(metaPath, ct).ConfigureAwait(false);

            foreach (string line in lines)
            {
                const string Prefix = "fetched-at=";

                if (line.StartsWith(Prefix, StringComparison.Ordinal))
                {
                    string value = line[Prefix.Length..];

                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long fetchedAtMs))
                    {
                        return DateTimeOffset.FromUnixTimeMilliseconds(fetchedAtMs);
                    }

                    break;
                }
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException) when (!File.Exists(metaPath))
        {
        }

        return null;
    }

    ValueTask IStorageHealthProbe.CheckAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_root))
        {
            throw new InvalidOperationException($"Cache root directory does not exist: {_root}");
        }

        return ValueTask.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to read metadata file {Path}; falling back to file info.")]
    private partial void LogMetaReadFallback(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to read metadata for sweep entry {Path}; falling back to file modification time.")]
    private partial void LogSweepMetaReadFailed(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete cached file entry {Key}.")]
    private partial void LogDeleteFailed(Exception ex, string key);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to update cache file timestamp {Key}.")]
    private partial void LogTouchFailed(Exception ex, string key);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to prune empty cache directories {Dir}.")]
    private partial void LogPruneDirectoryFailed(Exception ex, string? dir);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to read tag pointer {Key}.")]
    private partial void LogTagPointerReadFailed(Exception ex, string key);

    internal sealed class MetaInfo
    {
        public long Length { get; set; } = -1;
        public string ContentType { get; set; } = "application/octet-stream";
        public string Digest { get; set; } = string.Empty;
        public DateTimeOffset? FetchedAt { get; set; }
    }

    private sealed class BoundedReadStream(FileStream inner, long byteCount) : Stream
    {
        private long _remaining = byteCount;

        public override bool CanRead => true;
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
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            if (count > _remaining)
            {
                count = (int)_remaining;
            }

            int read = inner.Read(buffer, offset, count);
            _remaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            if (buffer.Length > _remaining)
            {
                buffer = buffer[..(int)_remaining];
            }

            int read = await inner.ReadAsync(buffer, ct).ConfigureAwait(false);
            _remaining -= read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
