namespace DockerMirror.Caching;

internal sealed record CachedContent
{
    public required Stream Stream { get; init; }
    public long Length { get; init; }
    public required string ContentType { get; init; }
    public required string Digest { get; init; }
    public DateTimeOffset? StoredAtUtc { get; init; }
}
