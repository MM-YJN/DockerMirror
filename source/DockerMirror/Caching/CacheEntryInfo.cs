namespace DockerMirror.Caching;

internal readonly record struct CacheEntryInfo(string Key, long Length, DateTimeOffset CreatedAtUtc, DateTimeOffset LastAccessedUtc);
