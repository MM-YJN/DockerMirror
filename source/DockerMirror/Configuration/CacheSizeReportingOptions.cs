namespace DockerMirror.Configuration;

public sealed class CacheSizeReportingOptions
{
    /// <summary>
    /// Whether periodic cache-size enumeration and metric reporting is enabled when
    /// eviction is disabled. Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// When eviction is enabled (<c>Mirror:Cache:Eviction:Enabled = true</c>), cache
    /// size metrics are always reported as a by-product of each eviction sweep, so this
    /// setting has no effect. When eviction is disabled, set this to
    /// <see langword="true"/> to still emit <c>dockermirror.cache.size.bytes</c> and
    /// <c>dockermirror.cache.size.entries</c> metrics on the interval defined by
    /// <see cref="Interval"/>. Requires <c>Mirror:Cache:Enabled</c> to be
    /// <see langword="true"/>.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>
    /// How often cache size is enumerated and reported when eviction is disabled.
    /// Defaults to 5 minutes.
    /// </summary>
    /// <remarks>
    /// Only used when <see cref="Enabled"/> is <see langword="true"/> and
    /// <c>Mirror:Cache:Eviction:Enabled</c> is <see langword="false"/>. When eviction
    /// is active the eviction <c>Interval</c> governs the schedule instead.
    /// </remarks>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);
}
