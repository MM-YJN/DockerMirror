namespace DockerMirror.Caching;

internal interface ICacheWriteHandle : IAsyncDisposable
{
    Stream Stream { get; }

    void SetMetadata(CacheEntryMetadata metadata);

    // Returns true when the computed hash matched ExpectedDigest and the entry was
    // promoted to the cache.  Returns false when the hash did not match: the temp
    // file is deleted and no cache entry is written.  Either way the caller's
    // finally-DisposeAsync is a safe no-op.
    ValueTask<bool> CommitAsync(CancellationToken ct);
}
