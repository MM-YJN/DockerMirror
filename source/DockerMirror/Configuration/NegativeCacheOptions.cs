using System.ComponentModel.DataAnnotations;

namespace DockerMirror.Configuration;

public sealed class NegativeCacheOptions
{
    public bool Enabled { get; set; } = true;

    public TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(60);

    [Range(1, int.MaxValue)]
    public int MaxEntries { get; set; } = 10000;
}
