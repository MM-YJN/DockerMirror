using System.Buffers;

namespace DockerMirror.Registry;

internal static partial class ProxyStreaming
{
    public const int StreamCopyBufferSize = 65536;

    internal static async Task<(long Copied, bool CacheOk)> TeeStreamAsync(
        Stream source, Stream clientOutput, Stream cacheOutput,
        string cacheKey, ILogger logger, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(StreamCopyBufferSize);
        long totalCopied = 0;
        bool cacheOk = true;

        try
        {
            while (true)
            {
                int read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                await clientOutput.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);

                if (cacheOk)
                {
                    try
                    {
                        await cacheOutput.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        LogTeeCacheFailed(logger, ex, cacheKey);
                        cacheOk = false;
                    }
                }

                totalCopied += read;
            }

            await clientOutput.FlushAsync(ct).ConfigureAwait(false);

            if (cacheOk)
            {
                try
                {
                    await cacheOutput.FlushAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogTeeCacheFailed(logger, ex, cacheKey);
                    cacheOk = false;
                }
            }

            return (totalCopied, cacheOk);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cache write failed during streaming tee for {CacheKey}.")]
    private static partial void LogTeeCacheFailed(ILogger logger, Exception ex, string cacheKey);
}
