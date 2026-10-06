using System.ComponentModel.DataAnnotations;

namespace DockerMirror.Configuration;

public sealed class CacheEvictionOptions
{
    public bool Enabled { get; set; }

    /// <summary>
    /// Eviction strategy for size-based eviction: <c>Oldest</c> evicts by creation time (oldest first);
    /// <c>Lru</c> evicts by last-access time (least-recently-used first). Does not affect <see cref="MaxAge"/>.
    /// </summary>
    public EvictionStrategy Strategy { get; set; } = EvictionStrategy.Oldest;

    public long? MaxSizeBytes { get; set; }

    /// <summary>
    /// Maximum age of entries measured from the time they were fetched from upstream.
    /// Entries older than this are evicted regardless of <see cref="Strategy"/>.
    /// </summary>
    public TimeSpan? MaxAge { get; set; }

    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(15);

    [Range(0.0, 1.0)]
    public double TargetUtilization { get; set; } = 0.9;
}
