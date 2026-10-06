using System.Text.Json.Serialization;

namespace DockerMirror.Diagnostics;

internal sealed record MirrorStatsResponse
{
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("uptime")]
    public string Uptime { get; init; } = string.Empty;

    [JsonPropertyName("upstream")]
    public UpstreamStats Upstream { get; init; } = new();

    [JsonPropertyName("cache")]
    public CacheStats Cache { get; init; } = new();

    [JsonPropertyName("storage")]
    public StorageStats Storage { get; init; } = new();
}

internal sealed record UpstreamStats
{
    [JsonPropertyName("registryUrl")]
    public string RegistryUrl { get; init; } = string.Empty;

    [JsonPropertyName("tokenService")]
    public string TokenService { get; init; } = string.Empty;

    [JsonPropertyName("defaultNamespace")]
    public string DefaultNamespace { get; init; } = string.Empty;
}

internal sealed record CacheStats
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("backend")]
    public string Backend { get; init; } = string.Empty;

    [JsonPropertyName("evictionEnabled")]
    public bool EvictionEnabled { get; init; }

    [JsonPropertyName("negativeCacheEnabled")]
    public bool NegativeCacheEnabled { get; init; }

    [JsonPropertyName("tagManifests")]
    public TagManifestStats TagManifests { get; init; } = new();

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; init; }

    [JsonPropertyName("entryCount")]
    public long EntryCount { get; init; }

    [JsonPropertyName("lastUpdatedUtc")]
    public string? LastUpdatedUtc { get; init; }

    [JsonPropertyName("negativeCacheEntries")]
    public int NegativeCacheEntries { get; init; }
}

internal sealed record TagManifestStats
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("ttl")]
    public TimeSpan Ttl { get; init; }
}

internal sealed record StorageStats
{
    [JsonPropertyName("healthy")]
    public bool? Healthy { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}
