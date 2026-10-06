using DockerMirror.Registry;

namespace DockerMirror.UnitTests.Registry;

public sealed class RegistryPathTests
{
    [Theory]
    [InlineData("nginx/manifests/latest", "nginx", "manifests", "latest")]
    [InlineData("nginx/manifests/sha256:abc123", "nginx", "manifests", "sha256:abc123")]
    [InlineData("nginx/manifests/2.0.0", "nginx", "manifests", "2.0.0")]
    [InlineData("library/nginx/manifests/latest", "library/nginx", "manifests", "latest")]
    [InlineData("org/app/manifests/v1.0", "org/app", "manifests", "v1.0")]
    [InlineData("org/grp/app/manifests/latest", "org/grp/app", "manifests", "latest")]
    [InlineData("a/b/c/d/manifests/latest", "a/b/c/d", "manifests", "latest")]
    public void Parse_Manifests_ReturnsCorrectParts(string path, string expectedName, string expectedResourceType, string expectedReference)
    {
        var result = RegistryPath.Parse(path);

        Assert.Equal(expectedName, result.Name);
        Assert.Equal(expectedResourceType, result.ResourceType);
        Assert.Equal(expectedReference, result.Reference);
    }

    [Theory]
    [InlineData("nginx/tags/list", "nginx", "tags", "list")]
    [InlineData("org/app/tags/list", "org/app", "tags", "list")]
    public void Parse_Tags_ReturnsCorrectParts(string path, string expectedName, string expectedResourceType, string expectedReference)
    {
        var result = RegistryPath.Parse(path);

        Assert.Equal(expectedName, result.Name);
        Assert.Equal(expectedResourceType, result.ResourceType);
        Assert.Equal(expectedReference, result.Reference);
    }

    [Theory]
    [InlineData("nginx/blobs/sha256:abc", "nginx", "blobs", "sha256:abc")]
    [InlineData("org/app/blobs/sha256:def", "org/app", "blobs", "sha256:def")]
    public void Parse_Blobs_ReturnsCorrectParts(string path, string expectedName, string expectedResourceType, string expectedReference)
    {
        var result = RegistryPath.Parse(path);

        Assert.Equal(expectedName, result.Name);
        Assert.Equal(expectedResourceType, result.ResourceType);
        Assert.Equal(expectedReference, result.Reference);
    }

    [Theory]
    [InlineData("nginx/referrers/sha256:abc", "nginx", "referrers", "sha256:abc")]
    [InlineData("org/app/referrers/sha256:abc", "org/app", "referrers", "sha256:abc")]
    public void Parse_Referrers_ReturnsCorrectParts(string path, string expectedName, string expectedResourceType, string expectedReference)
    {
        var result = RegistryPath.Parse(path);

        Assert.Equal(expectedName, result.Name);
        Assert.Equal(expectedResourceType, result.ResourceType);
        Assert.Equal(expectedReference, result.Reference);
    }

    [Theory]
    [InlineData("nginx/blobs/uploads", "nginx", "blobs/uploads", null)]
    [InlineData("nginx/blobs/uploads/uuid-x", "nginx", "blobs/uploads", "uuid-x")]
    [InlineData("org/app/blobs/uploads/session-id", "org/app", "blobs/uploads", "session-id")]
    public void Parse_BlobUploads_ReturnsCorrectParts(string path, string expectedName, string expectedResourceType, string? expectedReference)
    {
        var result = RegistryPath.Parse(path);

        Assert.Equal(expectedName, result.Name);
        Assert.Equal(expectedResourceType, result.ResourceType);
        Assert.Equal(expectedReference, result.Reference);
    }

    [Fact]
    public void Parse_EmptyString_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => RegistryPath.Parse(""));
    }

    [Fact]
    public void Parse_Null_ThrowsArgumentException()
    {
        // RegistryPath.Parse takes ReadOnlySpan<char>; a null string is implicitly
        // converted to an empty span, so the method throws ArgumentException
        // (path cannot be empty) rather than ArgumentNullException.
        Assert.Throws<ArgumentException>(() => RegistryPath.Parse([]));
    }

    [Theory]
    [InlineData("nginx")]
    [InlineData("nginx/latest")]
    [InlineData("some-random/not-a-resource")]
    public void Parse_NoResourceType_ThrowsInvalidOperationException(string path)
    {
        Assert.Throws<InvalidOperationException>(() => RegistryPath.Parse(path));
    }

    [Fact]
    public void Parse_ResourceTypeWithNoName_ThrowsInvalidOperationException()
    {
        Assert.Throws<InvalidOperationException>(() => RegistryPath.Parse("manifests/latest"));
    }

    [Theory]
    [InlineData("nginx/manifests/latest", "library/nginx")]
    [InlineData("org/app/manifests/latest", "org/app")]
    [InlineData("library/nginx/manifests/latest", "library/nginx")]
    public void RewrittenName_DefaultNamespace_AppliesCorrectly(string path, string expected)
    {
        var registryPath = RegistryPath.Parse(path);
        string result = registryPath.RewrittenName("library");
        Assert.Equal(expected, result);
    }

    [Fact]
    public void RewrittenName_CustomNamespace_AppliesCorrectly()
    {
        var registryPath = RegistryPath.Parse("nginx/manifests/latest");
        string result = registryPath.RewrittenName("custom-ns");
        Assert.Equal("custom-ns/nginx", result);
    }

    [Theory]
    [InlineData("nginx/manifests/latest", "repository:nginx:pull")]
    [InlineData("org/app/manifests/latest", "repository:org/app:pull")]
    [InlineData("nginx/referrers/sha256:abc", "repository:nginx:pull")]
    public void Scope_ReturnsCorrectFormat(string path, string expectedScope)
    {
        var registryPath = RegistryPath.Parse(path);
        Assert.Equal(expectedScope, registryPath.Scope);
    }

    [Fact]
    public void Parse_ManifestsSegmentsBeforeSlashCorrectly()
    {
        var result = RegistryPath.Parse("org/app/manifests/latest");
        Assert.Equal("org/app", result.Name);
        Assert.Equal("manifests", result.ResourceType);
        Assert.Equal("latest", result.Reference);
    }
}
