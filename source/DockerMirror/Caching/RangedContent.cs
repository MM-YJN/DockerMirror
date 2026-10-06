namespace DockerMirror.Caching;

internal sealed record RangedContent : IAsyncDisposable
{
    public required Stream Stream { get; init; }

    public int StatusCode { get; init; }

    public long ContentLength { get; init; } = -1;

    public required string ContentType { get; init; }

    public string? ContentRange { get; init; }

    public string Digest { get; init; } = string.Empty;

    public DateTimeOffset? StoredAtUtc { get; init; }

    public required IAsyncDisposable Owner { get; init; }

    public ValueTask DisposeAsync() => Owner.DisposeAsync();
}
