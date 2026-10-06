namespace DockerMirror.Diagnostics;

internal sealed class CacheStatsState
{
    private long _sizeBytes;
    private long _entryCount;
    private long _lastUpdateTicks;

    public long SizeBytes => Interlocked.Read(ref _sizeBytes);

    public long EntryCount => Interlocked.Read(ref _entryCount);

    public DateTimeOffset LastUpdateUtc
    {
        get
        {
            long ticks = Interlocked.Read(ref _lastUpdateTicks);
            return ticks == 0 ? DateTimeOffset.MinValue : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public void Update(long bytes, long entries)
    {
        Interlocked.Exchange(ref _sizeBytes, bytes);
        Interlocked.Exchange(ref _entryCount, entries);
        Interlocked.Exchange(ref _lastUpdateTicks, DateTimeOffset.UtcNow.Ticks);
    }
}
