using System.Diagnostics;

using DockerMirror.Configuration;
using DockerMirror.Diagnostics;

using Microsoft.Extensions.Options;

namespace DockerMirror.Caching;

internal sealed partial class CacheEvictionService(
    IContentStore store,
    IOptions<MirrorOptions> options,
    TimeProvider timeProvider,
    ILogger<CacheEvictionService> logger,
    MirrorMetrics metrics,
    CacheStatsState cacheStats) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        CacheOptions cache = options.Value.Cache;

        // IOptions is a fixed snapshot — neither flag can change at runtime.
        // Return immediately when both eviction and size reporting are disabled to
        // avoid an idle timer loop.
        if (!cache.Enabled || (!cache.Eviction.Enabled && !cache.SizeReporting.Enabled))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            if (store is ICacheMaintenance maintenance)
            {
                if (cache.Eviction.Enabled)
                {
                    try
                    {
                        await SweepAsync(maintenance, cache.Eviction, stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        LogSweepFailed(ex);
                    }
                }
                else if (cache.SizeReporting.Enabled)
                {
                    try
                    {
                        await EnumerateStatsOnlyAsync(maintenance, stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        LogStatsPassFailed(ex);
                    }
                }
            }

            TimeSpan interval = cache.SizeReporting.Enabled && !cache.Eviction.Enabled
                && cache.SizeReporting.Interval > TimeSpan.Zero
                ? cache.SizeReporting.Interval
                : cache.Eviction.Interval > TimeSpan.Zero ? cache.Eviction.Interval : TimeSpan.FromMinutes(15);

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task EnumerateStatsOnlyAsync(ICacheMaintenance maintenance, CancellationToken ct)
    {
        var entries = new List<CacheEntryInfo>();

        await foreach (CacheEntryInfo entry in maintenance.EnumerateAsync(ct).ConfigureAwait(false))
        {
            entries.Add(entry);
        }

        if (entries.Count == 0)
        {
            metrics.UpdateCacheSize(0, 0);
            cacheStats.Update(0, 0);
            return;
        }

        long totalBytes = entries.Sum(e => e.Length);
        metrics.UpdateCacheSize(totalBytes, entries.Count);
        cacheStats.Update(totalBytes, entries.Count);
    }

    private async Task SweepAsync(ICacheMaintenance maintenance, CacheEvictionOptions eviction, CancellationToken ct)
    {
        long startedTimestamp = Stopwatch.GetTimestamp();
        LogSweepStarting();

        var entries = new List<CacheEntryInfo>();

        await foreach (CacheEntryInfo entry in maintenance.EnumerateAsync(ct).ConfigureAwait(false))
        {
            entries.Add(entry);
        }

        if (entries.Count == 0)
        {
            LogSweepEmpty();
            metrics.UpdateCacheSize(0, 0);
            cacheStats.Update(0, 0);
            metrics.RecordSweepDuration(Stopwatch.GetElapsedTime(startedTimestamp).TotalSeconds);
            return;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        int deleted = 0;
        long freedBytes = 0L;

        if (eviction.MaxAge is { } maxAge)
        {
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                CacheEntryInfo entry = entries[i];

                if (now - entry.CreatedAtUtc > maxAge)
                {
                    try
                    {
                        await maintenance.DeleteAsync(entry.Key, ct).ConfigureAwait(false);
                        deleted++;
                        freedBytes += entry.Length;
                        entries.RemoveAt(i);
                        metrics.RecordEviction("max_age");
                        metrics.RecordEvictedBytes(entry.Length);
                    }
                    catch (Exception ex)
                    {
                        LogDeleteFailed(ex);
                    }
                }
            }
        }

        long remainingTotal = entries.Sum(static e => e.Length);
        int sizeEvicted = 0;

        if (eviction.MaxSizeBytes is { } maxSize)
        {
            if (remainingTotal > maxSize)
            {
                if (eviction.Strategy == EvictionStrategy.Lru)
                {
                    entries.Sort(static (a, b) => a.LastAccessedUtc.CompareTo(b.LastAccessedUtc));
                }
                else
                {
                    entries.Sort(static (a, b) => a.CreatedAtUtc.CompareTo(b.CreatedAtUtc));
                }

                double targetUtilization = Math.Clamp(eviction.TargetUtilization, 0.0, 1.0);
                long targetSize = (long)(maxSize * targetUtilization);

                foreach (CacheEntryInfo entry in entries)
                {
                    if (remainingTotal <= targetSize)
                    {
                        break;
                    }

                    try
                    {
                        await maintenance.DeleteAsync(entry.Key, ct).ConfigureAwait(false);
                        deleted++;
                        sizeEvicted++;
                        freedBytes += entry.Length;
                        remainingTotal -= entry.Length;
                        metrics.RecordEviction("max_size");
                        metrics.RecordEvictedBytes(entry.Length);
                    }
                    catch (Exception ex)
                    {
                        LogDeleteFailed(ex);
                    }
                }
            }
        }

        if (deleted > 0)
        {
            LogSweepCompleted(deleted, freedBytes);
        }

        metrics.UpdateCacheSize(remainingTotal, entries.Count - sizeEvicted);
        cacheStats.Update(remainingTotal, entries.Count - sizeEvicted);
        metrics.RecordSweepDuration(Stopwatch.GetElapsedTime(startedTimestamp).TotalSeconds);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cache eviction sweep failed.")]
    private partial void LogSweepFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Starting cache eviction sweep.")]
    private partial void LogSweepStarting();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache eviction sweep found no entries.")]
    private partial void LogSweepEmpty();

    [LoggerMessage(Level = LogLevel.Information, Message = "Cache eviction sweep completed: {Count} entries evicted ({Freed} bytes freed).")]
    private partial void LogSweepCompleted(int count, long freed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete entry during cache eviction sweep.")]
    private partial void LogDeleteFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cache stats enumeration failed.")]
    private partial void LogStatsPassFailed(Exception ex);
}
