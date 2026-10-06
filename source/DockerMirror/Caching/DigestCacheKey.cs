using System.Globalization;

namespace DockerMirror.Caching;

internal static class DigestCacheKey
{
    public static string FromDigest(in Digest digest)
    {
        // Build sharded key: <algo>/<aa>/<bb>/<hex>
        // where aa/bb are the first two hex byte pairs
        string hex = digest.Hex;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{digest.Algorithm}/{hex.AsSpan(0, 2)}/{hex.AsSpan(2, 2)}/{hex}");
    }
}
