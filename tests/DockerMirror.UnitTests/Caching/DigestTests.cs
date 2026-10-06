using DockerMirror.Caching;

namespace DockerMirror.UnitTests.Caching;

public sealed class DigestTests
{
    private const string ValidSha256Hex = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ValidSha512Hex = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsDigest_ValidSha256_ReturnsTrue(bool useLower)
    {
        string hex = useLower ? ValidSha256Hex : ValidSha256Hex.ToUpperInvariant();
        Assert.True(Digest.IsDigest($"sha256:{hex}"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsDigest_ValidSha512_ReturnsTrue(bool useLower)
    {
        string hex = useLower ? ValidSha512Hex : ValidSha512Hex.ToUpperInvariant();
        Assert.True(Digest.IsDigest($"sha512:{hex}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("latest")]
    [InlineData("sha256:")]
    [InlineData("sha256:short")]
    [InlineData("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!")]
    [InlineData("sha1:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("sha256:gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void IsDigest_Invalid_ReturnsFalse(string? reference)
    {
        Assert.False(Digest.IsDigest(reference));
    }

    [Fact]
    public void TryParse_ValidSha256_ReturnsTrue()
    {
        string reference = $"sha256:{ValidSha256Hex}";
        Assert.True(Digest.TryParse(reference, out Digest digest));
        Assert.Equal("sha256", digest.Algorithm);
        Assert.Equal(ValidSha256Hex, digest.Hex);
    }

    [Fact]
    public void TryParse_ValidSha512_ReturnsTrue()
    {
        string reference = $"sha512:{ValidSha512Hex}";
        Assert.True(Digest.TryParse(reference, out Digest digest));
        Assert.Equal("sha512", digest.Algorithm);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("latest")]
    [InlineData("sha256:too-short")]
    [InlineData("sha1:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void TryParse_Invalid_ReturnsFalse(string? reference)
    {
        Assert.False(Digest.TryParse(reference, out _));
    }

    [Fact]
    public void Parse_Valid_ReturnsDigest()
    {
        var digest = Digest.Parse($"sha256:{ValidSha256Hex}");
        Assert.Equal("sha256", digest.Algorithm);
    }

    [Fact]
    public void Parse_Invalid_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => Digest.Parse("latest"));
    }

    [Fact]
    public void Hex_IsLowercased()
    {
        string upperHex = ValidSha256Hex.ToUpperInvariant();
        var digest = Digest.Parse($"sha256:{upperHex}");
        Assert.Equal(ValidSha256Hex, digest.Hex);
    }

    [Fact]
    public void AlgorithmName_Sha256_ReturnsSHA256()
    {
        var digest = Digest.Parse($"sha256:{ValidSha256Hex}");
        Assert.Equal(System.Security.Cryptography.HashAlgorithmName.SHA256, digest.AlgorithmName);
    }

    [Fact]
    public void AlgorithmName_Sha512_ReturnsSHA512()
    {
        var digest = new Digest("sha512", ValidSha512Hex);
        Assert.Equal(System.Security.Cryptography.HashAlgorithmName.SHA512, digest.AlgorithmName);
    }

    [Fact]
    public void Canonical_ReturnsCorrectFormat()
    {
        var digest = Digest.Parse($"sha256:{ValidSha256Hex}");
        Assert.Equal($"sha256:{ValidSha256Hex}", digest.Canonical);
    }

    [Fact]
    public void ToString_ReturnsCanonical()
    {
        var digest = Digest.Parse($"sha256:{ValidSha256Hex}");
        Assert.Equal(digest.Canonical, digest.ToString());
    }
}
