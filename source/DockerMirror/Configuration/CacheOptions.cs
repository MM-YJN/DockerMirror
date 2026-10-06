using Microsoft.Extensions.Options;

namespace DockerMirror.Configuration;

public sealed class CacheOptions
{
    public bool Enabled { get; set; }

    public string Backend { get; set; } = "FileSystem";

    public FileSystemCacheOptions FileSystem { get; set; } = new();

    public S3CacheOptions S3 { get; set; } = new();

    public int WarmQueueCapacity { get; set; } = 256;

    [ValidateObjectMembers]
    public CacheEvictionOptions Eviction { get; set; } = new();

    [ValidateObjectMembers]
    public NegativeCacheOptions NegativeCache { get; set; } = new();

    public CacheSizeReportingOptions SizeReporting { get; set; } = new();

    public TagManifestOptions TagManifests { get; set; } = new();

    public CacheHeaderOptions Headers { get; set; } = new();

    public ListCacheOptions TagsList { get; set; } = new() { Enabled = true };

    public ListCacheOptions Catalog { get; set; } = new();
}
