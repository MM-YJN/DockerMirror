using DockerMirror.Caching;
using DockerMirror.Registry;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace DockerMirror.UnitTests.Registry;

public sealed class ConditionalRequestTests
{
    [Fact]
    public void ToETag_WrapsDigestInQuotes()
    {
        var digest = new Digest("sha256", "abc123");
        string etag = ConditionalRequest.ToETag(digest);

        Assert.Equal("\"sha256:abc123\"", etag);
    }

    [Fact]
    public void FormatETag_WrapsInQuotes()
    {
        string etag = ConditionalRequest.FormatETag("sha256:abc123");

        Assert.Equal("\"sha256:abc123\"", etag);
    }

    [Fact]
    public void IfNoneMatchMatches_NoHeader_ReturnsFalse()
    {
        var context = new DefaultHttpContext();

        bool result = ConditionalRequest.IfNoneMatchMatches(context.Request, "\"sha256:abc\"");

        Assert.False(result);
    }

    [Fact]
    public void IfNoneMatchMatches_ExactMatch_ReturnsTrue()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.IfNoneMatch = "\"sha256:abc\"";

        bool result = ConditionalRequest.IfNoneMatchMatches(context.Request, "\"sha256:abc\"");

        Assert.True(result);
    }

    [Fact]
    public void IfNoneMatchMatches_Mismatch_ReturnsFalse()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.IfNoneMatch = "\"sha256:def\"";

        bool result = ConditionalRequest.IfNoneMatchMatches(context.Request, "\"sha256:abc\"");

        Assert.False(result);
    }

    [Fact]
    public void IfNoneMatchMatches_WeakPrefix_ReturnsTrue()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.IfNoneMatch = "W/\"sha256:abc\"";

        bool result = ConditionalRequest.IfNoneMatchMatches(context.Request, "\"sha256:abc\"");

        Assert.True(result);
    }

    [Fact]
    public void IfNoneMatchMatches_SingleStar_ReturnsFalse()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.IfNoneMatch = "*";

        bool result = ConditionalRequest.IfNoneMatchMatches(context.Request, "\"sha256:abc\"");

        Assert.False(result);
    }

    [Fact]
    public void IfNoneMatchMatches_MultiValue_MatchesFirst()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.IfNoneMatch = "\"sha256:abc\", \"sha256:def\"";

        bool result = ConditionalRequest.IfNoneMatchMatches(context.Request, "\"sha256:abc\"");

        Assert.True(result);
    }

    [Fact]
    public void IfNoneMatchMatches_MultiValue_MatchesSecond()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.IfNoneMatch = "\"sha256:abc\", \"sha256:def\"";

        bool result = ConditionalRequest.IfNoneMatchMatches(context.Request, "\"sha256:def\"");

        Assert.True(result);
    }

    [Fact]
    public void IfNoneMatchMatches_StarInList_StillMatchesOtherToken()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.IfNoneMatch = "*, \"sha256:abc\"";

        bool result = ConditionalRequest.IfNoneMatchMatches(context.Request, "\"sha256:abc\"");

        Assert.True(result);
    }

    [Fact]
    public void IfNoneMatchMatches_WithoutQuotes_StillMatches()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.IfNoneMatch = "sha256:abc";

        bool result = ConditionalRequest.IfNoneMatchMatches(context.Request, "\"sha256:abc\"");

        Assert.True(result);
    }

    [Fact]
    public void ApplyValidatorHeaders_SetsAllHeaders()
    {
        var context = new DefaultHttpContext();
        TimeProvider time = TimeProvider.System;

        ConditionalRequest.ApplyValidatorHeaders(
            context.Response, "\"sha256:abc\"", "sha256:abc", "public, max-age=300, immutable",
            storedAtUtc: null, time);

        Assert.Equal("\"sha256:abc\"", context.Response.Headers.ETag.ToString());
        Assert.Equal("sha256:abc", context.Response.Headers["Docker-Content-Digest"].ToString());
        Assert.Equal("public, max-age=300, immutable", context.Response.Headers.CacheControl.ToString());
        Assert.Equal("0", context.Response.Headers.Age.ToString());
    }

    [Fact]
    public void ApplyValidatorHeaders_WithStoredAt_SetsAge()
    {
        var context = new DefaultHttpContext();
        TimeProvider time = TimeProvider.System;
        DateTimeOffset storedAt = time.GetUtcNow() - TimeSpan.FromSeconds(10);

        ConditionalRequest.ApplyValidatorHeaders(
            context.Response, "\"sha256:abc\"", "sha256:abc", "public, max-age=300",
            storedAtUtc: storedAt, time);

        int age = int.Parse(context.Response.Headers.Age.ToString());
        Assert.True(age is >= 10 and <= 11, $"Expected age ~10, got {age}");
    }

    [Fact]
    public void ApplyValidatorHeaders_NullETag_OmitsETag()
    {
        var context = new DefaultHttpContext();
        TimeProvider time = TimeProvider.System;

        ConditionalRequest.ApplyValidatorHeaders(
            context.Response, etag: null, "sha256:abc", "public, max-age=60",
            storedAtUtc: null, time);

        Assert.False(context.Response.Headers.ContainsKey("ETag"));
        Assert.Equal("public, max-age=60", context.Response.Headers.CacheControl.ToString());
        Assert.Equal("0", context.Response.Headers.Age.ToString());
    }
}
