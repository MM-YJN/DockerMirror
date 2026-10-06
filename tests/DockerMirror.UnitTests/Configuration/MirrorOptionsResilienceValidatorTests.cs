using DockerMirror.Configuration;

using Microsoft.Extensions.Options;

namespace DockerMirror.UnitTests.Configuration;

public sealed class MirrorOptionsResilienceValidatorTests
{
    [Fact]
    public void Validate_Succeeds_WithAllDefaults()
    {
        var options = new MirrorOptions();

        ValidateOptionsResult result = new MirrorOptionsResilienceValidator().Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_Succeeds_WhenResilienceDisabled()
    {
        var options = new MirrorOptions();
        options.Upstream.Resilience.Enabled = false;

        ValidateOptionsResult result = new MirrorOptionsResilienceValidator().Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_Fails_ForNegativeTokenCacheTtl()
    {
        var options = new MirrorOptions();
        options.Upstream.TokenCacheTtl = TimeSpan.FromMinutes(-5);

        ValidateOptionsResult result = new MirrorOptionsResilienceValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("TokenCacheTtl", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_Fails_ForNegativeBaseDelay()
    {
        var options = new MirrorOptions();
        options.Upstream.Resilience.BaseDelay = TimeSpan.FromSeconds(-1);

        ValidateOptionsResult result = new MirrorOptionsResilienceValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("BaseDelay", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_Fails_ForNegativeAttemptTimeout()
    {
        var options = new MirrorOptions();
        options.Upstream.Resilience.AttemptTimeout = TimeSpan.FromSeconds(-1);

        ValidateOptionsResult result = new MirrorOptionsResilienceValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("AttemptTimeout", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_Fails_ForNegativeTotalRequestTimeout()
    {
        var options = new MirrorOptions();
        options.Upstream.Resilience.TotalRequestTimeout = TimeSpan.FromSeconds(-1);

        ValidateOptionsResult result = new MirrorOptionsResilienceValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("TotalRequestTimeout", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_Fails_ForNegativeHeadersTimeout()
    {
        var options = new MirrorOptions();
        options.Upstream.Resilience.HeadersTimeout = TimeSpan.FromSeconds(-1);

        ValidateOptionsResult result = new MirrorOptionsResilienceValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("HeadersTimeout", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_Fails_ForNegativeSamplingDuration()
    {
        var options = new MirrorOptions();
        options.Upstream.Resilience.SamplingDuration = TimeSpan.FromSeconds(-1);

        ValidateOptionsResult result = new MirrorOptionsResilienceValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("SamplingDuration", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_Fails_ForNegativeBreakDuration()
    {
        var options = new MirrorOptions();
        options.Upstream.Resilience.BreakDuration = TimeSpan.FromSeconds(-1);

        ValidateOptionsResult result = new MirrorOptionsResilienceValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("BreakDuration", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_Fails_ForNegativeConnectTimeout()
    {
        var options = new MirrorOptions();
        options.Upstream.Resilience.ConnectTimeout = TimeSpan.FromSeconds(-1);

        ValidateOptionsResult result = new MirrorOptionsResilienceValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("ConnectTimeout", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_ReportsAllErrors_WhenMultipleFieldsInvalid()
    {
        var options = new MirrorOptions();
        options.Upstream.TokenCacheTtl = TimeSpan.FromMinutes(-1);
        options.Upstream.Resilience.BaseDelay = TimeSpan.FromSeconds(-2);

        ValidateOptionsResult result = new MirrorOptionsResilienceValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("TokenCacheTtl", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BaseDelay", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public void Validate_Succeeds_ForNonNegativeTimeSpans(int seconds)
    {
        var options = new MirrorOptions();
        var ts = TimeSpan.FromSeconds(seconds);
        options.Upstream.Resilience.BaseDelay = ts;
        options.Upstream.Resilience.AttemptTimeout = ts;
        options.Upstream.Resilience.TotalRequestTimeout = ts;
        options.Upstream.Resilience.HeadersTimeout = ts;
        options.Upstream.Resilience.SamplingDuration = ts;
        options.Upstream.Resilience.BreakDuration = ts;
        options.Upstream.Resilience.ConnectTimeout = ts;

        ValidateOptionsResult result = new MirrorOptionsResilienceValidator().Validate(null, options);

        Assert.True(result.Succeeded);
    }
}
