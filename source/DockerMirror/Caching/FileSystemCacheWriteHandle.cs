using System.Security.Cryptography;

namespace DockerMirror.Caching;

internal sealed partial class FileSystemCacheWriteHandle(
    string tempPath,
    string targetPath,
    Digest expectedDigest,
    ILogger<FileSystemContentStore> logger
    ) : ICacheWriteHandle
{
    private string _contentType = "application/octet-stream";
    private bool _committed;

    // Set on the mismatch path: stream + temp already cleaned up inside CommitAsync,
    // so DisposeAsync must not touch them a second time.
    private bool _cleanedUp;

    public Stream Stream { get; } = new HashingWriteStream(
        new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan),
        expectedDigest.AlgorithmName);

    public void SetMetadata(CacheEntryMetadata metadata) => _contentType = metadata.ContentType;

    public async ValueTask<bool> CommitAsync(CancellationToken ct)
    {
        await Stream.FlushAsync(ct).ConfigureAwait(false);

        var hashingStream = (HashingWriteStream)Stream;
        string computedHex = hashingStream.GetComputedHex();
        long actualLength = hashingStream.BytesWritten;
        string computedDigest = $"{expectedDigest.Algorithm}:{computedHex}";

        await Stream.DisposeAsync().ConfigureAwait(false);

        if (!string.Equals(expectedDigest.Hex, computedHex, StringComparison.Ordinal))
        {
            LogDigestMismatch(expectedDigest.Canonical, computedDigest, tempPath);

            try
            {
                File.Delete(tempPath);
            }
            catch (Exception ex)
            {
                LogDeleteFailed(ex, tempPath);
            }

            // Mark as cleaned up so DisposeAsync (called from the caller's finally) is a no-op.
            _cleanedUp = true;
            return false;
        }

        await Task.Run(() => File.Move(tempPath, targetPath, overwrite: true), ct).ConfigureAwait(false);

        string metaPath = targetPath + ".meta";
        var metaLines = new List<string>(4)
        {
            $"length={actualLength}",
            $"content-type={_contentType}",
            $"digest={computedDigest}",
            $"fetched-at={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
        };

        await File.WriteAllLinesAsync(metaPath, metaLines, ct).ConfigureAwait(false);

        _committed = true;
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        // Committed: stream already disposed inside CommitAsync after successful move.
        // Cleaned up: stream + temp already handled in CommitAsync on digest mismatch.
        // Neither: normal rollback — close the temp stream and delete the partial file.
        if (_committed || _cleanedUp)
        {
            return;
        }

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Digest mismatch for {ExpectedDigest}: computed {ComputedDigest}. Temp file {Path} deleted, not cached.")]
    private partial void LogDigestMismatch(string expectedDigest, string computedDigest, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete temporary cache file {Path}.")]
    private partial void LogDeleteFailed(Exception ex, string path);

    private sealed class HashingWriteStream(Stream inner, HashAlgorithmName algorithm) : Stream
    {
        private IncrementalHash? _hash = IncrementalHash.CreateHash(algorithm);
        private long _bytesWritten;

        private IncrementalHash HashOrThrow => _hash ?? throw new ObjectDisposedException(nameof(HashingWriteStream));

        public long BytesWritten => _bytesWritten;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            HashOrThrow.AppendData(buffer, offset, count);
            _bytesWritten += count;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await inner.WriteAsync(buffer, ct).ConfigureAwait(false);
            HashOrThrow.AppendData(buffer.Span);
            _bytesWritten += buffer.Length;
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            await inner.WriteAsync(buffer.AsMemory(offset, count), ct).ConfigureAwait(false);
            HashOrThrow.AppendData(buffer, offset, count);
            _bytesWritten += count;
        }

        public string GetComputedHex()
        {
            byte[] hash = HashOrThrow.GetHashAndReset();
            _hash?.Dispose();
            _hash = null;
            return Convert.ToHexStringLower(hash);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                _hash?.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
