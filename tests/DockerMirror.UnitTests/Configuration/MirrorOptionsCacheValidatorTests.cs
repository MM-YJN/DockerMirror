using DockerMirror.Configuration;

using Microsoft.Extensions.Options;

namespace DockerMirror.UnitTests.Configuration;

public sealed class MirrorOptionsCacheValidatorTests
{
    [Fact]
    public void Validate_Succeeds_WithAllDefaults()
    {
        var options = new MirrorOptions();

        ValidateOptionsResult result = new MirrorOptionsCacheValidator().Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("FileSystem")]
    [InlineData("filesystem")]
    [InlineData("S3")]
    [InlineData("s3")]
    public void Validate_Succeeds_ForAllowedBackends(string backend)
    {
        var options = new MirrorOptions();
        options.Cache.Backend = backend;

        ValidateOptionsResult result = new MirrorOptionsCacheValidator().Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Memory")]
    [InlineData("s33")]
    [InlineData("FILE")]
    public void Validate_Fails_ForInvalidBackend(string backend)
    {
        var options = new MirrorOptions();
        options.Cache.Backend = backend;

        ValidateOptionsResult result = new MirrorOptionsCacheValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Mirror:Cache:Backend", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_Fails_ForInvalidWarmQueueCapacity(int capacity)
    {
        var options = new MirrorOptions();
        options.Cache.WarmQueueCapacity = capacity;

        ValidateOptionsResult result = new MirrorOptionsCacheValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Mirror:Cache:WarmQueueCapacity", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_Fails_ForEmptyDirectory()
    {
        var options = new MirrorOptions();
        options.Cache.FileSystem.Directory = "";

        ValidateOptionsResult result = new MirrorOptionsCacheValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Mirror:Cache:FileSystem:Directory", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../cache")]
    [InlineData("/var/cache/../mirror")]
    [InlineData("foo/../bar")]
    [InlineData(@"C:\data\..\cache")]
    public void Validate_Fails_ForDirectoryWithTraversal(string directory)
    {
        var options = new MirrorOptions();
        options.Cache.FileSystem.Directory = directory;

        ValidateOptionsResult result = new MirrorOptionsCacheValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Mirror:Cache:FileSystem:Directory", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/var/cache/mirror")]
    [InlineData("docker-mirror-cache")]
    [InlineData(@"C:\cache")]
    [InlineData("rel/ative/path")]
    [InlineData("..docker")] // contains ".." but not as a path segment
    public void Validate_Succeeds_ForValidDirectory(string directory)
    {
        var options = new MirrorOptions();
        options.Cache.FileSystem.Directory = directory;

        ValidateOptionsResult result = new MirrorOptionsCacheValidator().Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_Fails_ForNegativeEvictionInterval()
    {
        var options = new MirrorOptions();
        options.Cache.Eviction.Interval = TimeSpan.FromMinutes(-1);

        ValidateOptionsResult result = new MirrorOptionsCacheValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Mirror:Cache:Eviction:Interval", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1L)]
    [InlineData(1024L)]
    public void Validate_Succeeds_ForValidMaxSizeBytes(long? maxSize)
    {
        var options = new MirrorOptions();
        options.Cache.Eviction.MaxSizeBytes = maxSize;

        ValidateOptionsResult result = new MirrorOptionsCacheValidator().Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void Validate_Fails_ForInvalidMaxSizeBytes(long maxSize)
    {
        var options = new MirrorOptions();
        options.Cache.Eviction.MaxSizeBytes = maxSize;

        ValidateOptionsResult result = new MirrorOptionsCacheValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Mirror:Cache:Eviction:MaxSizeBytes", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_Fails_ForNegativeMaxAge()
    {
        var options = new MirrorOptions();
        options.Cache.Eviction.MaxAge = TimeSpan.FromHours(-1);

        ValidateOptionsResult result = new MirrorOptionsCacheValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Mirror:Cache:Eviction:MaxAge", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_Succeeds_ForZeroMaxAge()
    {
        var options = new MirrorOptions();
        options.Cache.Eviction.MaxAge = TimeSpan.Zero;

        ValidateOptionsResult result = new MirrorOptionsCacheValidator().Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_Succeeds_ForNullMaxAge()
    {
        var options = new MirrorOptions();
        options.Cache.Eviction.MaxAge = null;

        ValidateOptionsResult result = new MirrorOptionsCacheValidator().Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_Fails_ForNegativeSizeReportingInterval()
    {
        var options = new MirrorOptions();
        options.Cache.SizeReporting.Interval = TimeSpan.FromMinutes(-1);

        ValidateOptionsResult result = new MirrorOptionsCacheValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Mirror:Cache:SizeReporting:Interval", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_Fails_ForNegativeTagManifestsTtl()
    {
        var options = new MirrorOptions();
        options.Cache.TagManifests.Ttl = TimeSpan.FromMinutes(-1);

        ValidateOptionsResult result = new MirrorOptionsCacheValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Mirror:Cache:TagManifests:Ttl", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_Fails_ForNegativeNegativeCacheTtl()
    {
        var options = new MirrorOptions();
        options.Cache.NegativeCache.Ttl = TimeSpan.FromSeconds(-1);

        ValidateOptionsResult result = new MirrorOptionsCacheValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Mirror:Cache:NegativeCache:Ttl", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_ReportsAllErrors_WhenMultipleFieldsInvalid()
    {
        var options = new MirrorOptions();
        options.Cache.Backend = "Memory";
        options.Cache.WarmQueueCapacity = 0;
        options.Cache.Eviction.Interval = TimeSpan.FromMinutes(-1);

        ValidateOptionsResult result = new MirrorOptionsCacheValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Backend", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WarmQueueCapacity", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Eviction:Interval", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }
}
