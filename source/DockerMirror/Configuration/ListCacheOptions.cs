namespace DockerMirror.Configuration;

public sealed class ListCacheOptions
{
    public bool Enabled { get; set; }

    public TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(60);

    public int MaxEntries { get; set; } = 1024;

    public long MaxBodyBytes { get; set; } = 1_048_576;
}
