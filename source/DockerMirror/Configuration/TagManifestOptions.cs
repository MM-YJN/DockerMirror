namespace DockerMirror.Configuration;

public sealed class TagManifestOptions
{
    public bool Enabled { get; set; } = true;

    public TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(5);

    public bool ConditionalRevalidation { get; set; } = true;
}
