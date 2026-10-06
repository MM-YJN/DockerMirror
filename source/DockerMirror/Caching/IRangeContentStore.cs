namespace DockerMirror.Caching;

internal interface IRangeContentStore
{
    ValueTask<RangedContent?> TryGetRangeAsync(string key, string rangeHeader, CancellationToken ct);
}
