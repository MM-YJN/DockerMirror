using DockerMirror.Caching;

namespace DockerMirror.UnitTests.Caching;

public sealed class TagCacheKeyTests
{
    [Fact]
    public void From_ProducesExpectedFormat()
    {
        string key = TagCacheKey.From("library/nginx", "latest", null);

        Assert.StartsWith("tags/", key);
        string[] parts = key.Split('/');
        Assert.Equal(4, parts.Length); // tags, aa, bb, hex
        Assert.Equal("tags", parts[0]);
        Assert.Equal(2, parts[1].Length);
        Assert.Equal(2, parts[2].Length);
        Assert.Equal(64, parts[3].Length);
    }

    [Fact]
    public void From_SameInputsProduceSameKey()
    {
        string key1 = TagCacheKey.From("library/nginx", "latest", "application/json");
        string key2 = TagCacheKey.From("library/nginx", "latest", "application/json");

        Assert.Equal(key1, key2);
    }

    [Fact]
    public void From_DifferentTagProduceDifferentKey()
    {
        string key1 = TagCacheKey.From("library/nginx", "latest", null);
        string key2 = TagCacheKey.From("library/nginx", "v1.0", null);

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void From_DifferentNameProduceDifferentKey()
    {
        string key1 = TagCacheKey.From("library/nginx", "latest", null);
        string key2 = TagCacheKey.From("library/alpine", "latest", null);

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void From_DifferentAcceptProduceDifferentKey()
    {
        string key1 = TagCacheKey.From("library/nginx", "latest", "application/vnd.docker.distribution.manifest.v2+json");
        string key2 = TagCacheKey.From("library/nginx", "latest", "application/vnd.oci.image.manifest.v1+json");

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void NormalizeAccept_EmptyOrWhitespace_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, TagCacheKey.NormalizeAccept(null));
        Assert.Equal(string.Empty, TagCacheKey.NormalizeAccept(""));
        Assert.Equal(string.Empty, TagCacheKey.NormalizeAccept("   "));
    }

    [Fact]
    public void NormalizeAccept_SingleMediaType_Lowercased()
    {
        Assert.Equal("application/json", TagCacheKey.NormalizeAccept("application/json"));
        Assert.Equal("application/json", TagCacheKey.NormalizeAccept("Application/JSON"));
    }

    [Fact]
    public void NormalizeAccept_MultipleReordered_SameResult()
    {
        string a = TagCacheKey.NormalizeAccept("application/vnd.docker.distribution.manifest.v2+json, application/vnd.oci.image.manifest.v1+json");
        string b = TagCacheKey.NormalizeAccept("application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.v2+json");

        Assert.Equal(a, b);
    }

    [Fact]
    public void NormalizeAccept_WithWhitespace_SortedAndLowercased()
    {
        string result = TagCacheKey.NormalizeAccept("  text/plain , TEXT/html ");

        Assert.Equal("text/html,text/plain", result);
    }

    [Fact]
    public void From_AcceptReordering_SameKey()
    {
        string key1 = TagCacheKey.From("library/nginx", "latest", "type-a, type-b");
        string key2 = TagCacheKey.From("library/nginx", "latest", "type-b, type-a");

        Assert.Equal(key1, key2);
    }

    [Fact]
    public void From_AcceptCaseDifference_SameKey()
    {
        string key1 = TagCacheKey.From("library/nginx", "latest", "Type-A, Type-B");
        string key2 = TagCacheKey.From("library/nginx", "latest", "type-a, type-b");

        Assert.Equal(key1, key2);
    }

    [Fact]
    public void From_AcceptQualityValues_DifferentKeysForDifferentQ()
    {
        string key1 = TagCacheKey.From("library/nginx", "latest", "type-a;q=0.5, type-b");
        string key2 = TagCacheKey.From("library/nginx", "latest", "type-a, type-b;q=0.5");

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void From_RewrittenNameMatters()
    {
        string key1 = TagCacheKey.From("nginx", "latest", null);
        string key2 = TagCacheKey.From("library/nginx", "latest", null);

        Assert.NotEqual(key1, key2);
    }
}
