using DockerMirror.Configuration;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DockerMirror.UnitTests.Configuration;

public sealed class MirrorOptionsBasePathTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("/", "")]
    public void NormalizeBasePath_ReturnsEmpty_ForEmptyOrSlashValues(string? input, string expected)
    {
        string result = MirrorOptions.NormalizeBasePath(input);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("mirror", "/mirror")]
    [InlineData("/mirror", "/mirror")]
    [InlineData("/mirror/", "/mirror")]
    [InlineData(" /mirror/ ", "/mirror")]
    [InlineData("/docker", "/docker")]
    [InlineData("docker", "/docker")]
    [InlineData("/docker/", "/docker")]
    public void NormalizeBasePath_NormalizesBasicPrefixes(string input, string expected)
    {
        string result = MirrorOptions.NormalizeBasePath(input);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("feeds/internal", "/feeds/internal")]
    [InlineData("/feeds/internal", "/feeds/internal")]
    [InlineData("/feeds/internal/", "/feeds/internal")]
    [InlineData(" /feeds/internal/ ", "/feeds/internal")]
    public void NormalizeBasePath_NormalizesMultiSegmentPrefixes(string input, string expected)
    {
        string result = MirrorOptions.NormalizeBasePath(input);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void NormalizedBasePath_ReflectsBasePath()
    {
        var options = new MirrorOptions { BasePath = "/mirror/" };

        Assert.Equal("/mirror", options.NormalizedBasePath);
    }

    [Fact]
    public void NormalizedBasePath_ReturnsEmpty_WhenBasePathIsNull()
    {
        var options = new MirrorOptions { BasePath = null };

        Assert.Equal("", options.NormalizedBasePath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/mirror")]
    [InlineData("/docker")]
    [InlineData("/docker/")]
    [InlineData(" /docker/ ")]
    [InlineData("/feeds/internal")]
    public void Validator_AcceptsValidValues(string? value)
    {
        var options = new MirrorOptions { BasePath = value };
        var validator = new MirrorOptionsBasePathValidator();

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("../etc")]
    [InlineData("/etc/../passwd")]
    [InlineData("/path..segment")]
    public void Validator_RejectsDoubleDot(string value)
    {
        var options = new MirrorOptions { BasePath = value };
        var validator = new MirrorOptionsBasePathValidator();

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("..", result.FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("//mirror")]
    [InlineData("/mirror//v2")]
    public void Validator_RejectsDoubleSlash(string value)
    {
        var options = new MirrorOptions { BasePath = value };
        var validator = new MirrorOptionsBasePathValidator();

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("//", result.FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/mirror?", "?")]
    [InlineData("/mirror#one", "#")]
    [InlineData("/mirror:8080", ":")]
    [InlineData("/{path}", "{")]
    [InlineData("/mirror}", "}")]
    [InlineData("/mirror*", "*")]
    public void Validator_RejectsDisallowedCharacters(string value, string disallowed)
    {
        var options = new MirrorOptions { BasePath = value };
        var validator = new MirrorOptionsBasePathValidator();

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(disallowed, result.FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/mir ror")]
    public void Validator_RejectsInternalWhitespace(string value)
    {
        var options = new MirrorOptions { BasePath = value };
        var validator = new MirrorOptionsBasePathValidator();

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("whitespace", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateOnStart_FailsWhenBasePathHasDoubleDot()
    {
        var services = new ServiceCollection();
        services.AddOptions<MirrorOptions>()
            .Configure(o => o.BasePath = "../etc");
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsBasePathValidator>();
        using ServiceProvider sp = services.BuildServiceProvider();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<IOptions<MirrorOptions>>().Value);

        Assert.Contains("..", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateOnStart_SucceedsWhenBasePathIsNull()
    {
        var services = new ServiceCollection();
        services.AddOptions<MirrorOptions>()
            .Configure(o => o.BasePath = null);
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsBasePathValidator>();
        using ServiceProvider sp = services.BuildServiceProvider();

        MirrorOptions options = sp.GetRequiredService<IOptions<MirrorOptions>>().Value;

        Assert.Null(options.BasePath);
    }

    [Fact]
    public void ValidateOnStart_SucceedsWhenBasePathIsValid()
    {
        var services = new ServiceCollection();
        services.AddOptions<MirrorOptions>()
            .Configure(o => o.BasePath = "/docker");
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsBasePathValidator>();
        using ServiceProvider sp = services.BuildServiceProvider();

        MirrorOptions options = sp.GetRequiredService<IOptions<MirrorOptions>>().Value;

        Assert.Equal("/docker", options.BasePath);
    }
}
