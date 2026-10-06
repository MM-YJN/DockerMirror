using DockerMirror.Caching;

namespace DockerMirror.UnitTests.Caching;

public sealed class DigestCacheKeyTests
{
    [Fact]
    public void FromDigest_CreatesShardedPath()
    {
        string hex = "ab1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcd";
        var digest = Digest.Parse($"sha256:{hex}");
        string key = DigestCacheKey.FromDigest(digest);

        Assert.Equal($"sha256/ab/12/{hex}", key);
    }

    [Fact]
    public void FromDigest_Sha512_CreatesShardedPath()
    {
        string hex = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6abcd1234";
        var digest = new Digest("sha512", hex);
        string key = DigestCacheKey.FromDigest(digest);

        Assert.StartsWith("sha512/a1/b2/", key, StringComparison.Ordinal);
        Assert.EndsWith(hex, key, StringComparison.Ordinal);
    }

    [Fact]
    public void FromDigest_AllZeros_ShardsCorrectly()
    {
        string hex = new('0', 64);
        var digest = Digest.Parse($"sha256:{hex}");
        string key = DigestCacheKey.FromDigest(digest);

        Assert.Equal($"sha256/00/00/{hex}", key);
    }
}
