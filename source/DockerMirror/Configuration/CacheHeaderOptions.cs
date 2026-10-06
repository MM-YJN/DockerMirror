namespace DockerMirror.Configuration;

public sealed class CacheHeaderOptions
{
    public bool Enabled { get; set; } = true;

    public TimeSpan ImmutableMaxAge { get; set; } = TimeSpan.FromDays(365);
}
