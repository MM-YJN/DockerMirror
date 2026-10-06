namespace DockerMirror.Caching;

internal interface ICacheMaintenance
{
    IAsyncEnumerable<CacheEntryInfo> EnumerateAsync(CancellationToken ct);

    ValueTask DeleteAsync(string key, CancellationToken ct);

    ValueTask TouchAsync(string key, CancellationToken ct);
}
