namespace DockerMirror.Registry;

// Describes the caching policy for a response. The Cache-Control header string is
// precomputed at construction time so it is not re-allocated on every response.
internal readonly record struct ResponseCachePolicy(bool Immutable, TimeSpan MaxAge)
{
    private readonly string _cacheControl = BuildCacheControlString(Immutable, MaxAge);

    internal string BuildCacheControl() => _cacheControl;

    private static string BuildCacheControlString(bool immutable, TimeSpan maxAge)
    {
        long seconds = (long)maxAge.TotalSeconds;
        return immutable
            ? $"public, max-age={seconds}, immutable"
            : $"public, max-age={seconds}";
    }
}
