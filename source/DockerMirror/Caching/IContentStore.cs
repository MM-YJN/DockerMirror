namespace DockerMirror.Caching;

internal interface IContentStore
{
    ValueTask<bool> ExistsAsync(string key, CancellationToken ct);

    ValueTask<CachedContent?> TryGetAsync(string key, CancellationToken ct);

    ValueTask<ICacheWriteHandle> BeginWriteAsync(string key, Digest expectedDigest, CancellationToken ct);
}
