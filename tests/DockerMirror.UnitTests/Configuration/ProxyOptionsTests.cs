using DockerMirror.Configuration;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DockerMirror.UnitTests.Configuration;

public sealed class ProxyOptionsTests
{
    [Fact]
    public void Resolve_ReturnsNull_WhenNoProxyConfigured()
    {
        var proxy = new ProxyOptions();

        Assert.Null(proxy.Resolve(UpstreamEndpoint.Registry));
        Assert.Null(proxy.Resolve(UpstreamEndpoint.Auth));
        Assert.Null(proxy.Resolve(UpstreamEndpoint.Cdn));
    }

    [Fact]
    public void Resolve_UsesGlobalUrl_WhenNoPerEndpointOverride()
    {
        var proxy = new ProxyOptions { Url = "http://proxy.corp.example:8080" };

        Assert.Equal("http://proxy.corp.example:8080", proxy.Resolve(UpstreamEndpoint.Registry));
        Assert.Equal("http://proxy.corp.example:8080", proxy.Resolve(UpstreamEndpoint.Auth));
        Assert.Equal("http://proxy.corp.example:8080", proxy.Resolve(UpstreamEndpoint.Cdn));
    }

    [Fact]
    public void Resolve_UsesPerEndpointOverride_WhenSet()
    {
        var proxy = new ProxyOptions
        {
            Url = "http://proxy.corp.example:8080",
            Registry = "http://registry-proxy.corp.example:3128",
        };

        Assert.Equal("http://registry-proxy.corp.example:3128", proxy.Resolve(UpstreamEndpoint.Registry));
        Assert.Equal("http://proxy.corp.example:8080", proxy.Resolve(UpstreamEndpoint.Auth));
        Assert.Equal("http://proxy.corp.example:8080", proxy.Resolve(UpstreamEndpoint.Cdn));
    }

    [Fact]
    public void Resolve_PerEndpointOverrideFallsBackToGlobal_WhenOverrideIsEmpty()
    {
        var proxy = new ProxyOptions
        {
            Url = "http://proxy.corp.example:8080",
            Registry = string.Empty,
        };

        Assert.Equal("http://proxy.corp.example:8080", proxy.Resolve(UpstreamEndpoint.Registry));
    }

    [Fact]
    public void Resolve_PerEndpointOverrideFallsBackToGlobal_WhenOverrideIsNull()
    {
        var proxy = new ProxyOptions
        {
            Url = "http://proxy.corp.example:8080",
            Registry = null,
        };

        Assert.Equal("http://proxy.corp.example:8080", proxy.Resolve(UpstreamEndpoint.Registry));
    }

    [Fact]
    public void GetValidationErrors_ReturnsEmpty_WhenAllNull()
    {
        var proxy = new ProxyOptions();

        IReadOnlyList<string> errors = proxy.GetValidationErrors();

        Assert.Empty(errors);
    }

    [Fact]
    public void GetValidationErrors_ReturnsEmpty_WhenAllValid()
    {
        var proxy = new ProxyOptions
        {
            Url = "http://proxy.corp.example:8080",
            Registry = "http://registry-proxy.corp.example:3128",
        };

        IReadOnlyList<string> errors = proxy.GetValidationErrors();

        Assert.Empty(errors);
    }

    [Fact]
    public void GetValidationErrors_ReturnsOneError_PerInvalidEntry()
    {
        var proxy = new ProxyOptions
        {
            Url = "not-a-valid-url",
            Registry = "also-invalid",
        };

        IReadOnlyList<string> errors = proxy.GetValidationErrors();

        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Contains("Url") && e.Contains("not-a-valid-url"));
        Assert.Contains(errors, e => e.Contains("Registry") && e.Contains("also-invalid"));
    }

    [Fact]
    public void GetValidationErrors_IgnoresEmptyStrings()
    {
        var proxy = new ProxyOptions
        {
            Url = null,
            Registry = string.Empty,
        };

        IReadOnlyList<string> errors = proxy.GetValidationErrors();

        Assert.Empty(errors);
    }

    [Fact]
    public void GetValidationErrors_RejectsInvalidUri()
    {
        var proxy = new ProxyOptions
        {
            Url = "not a valid url",
        };

        IReadOnlyList<string> errors = proxy.GetValidationErrors();

        Assert.Single(errors);
        Assert.Contains("absolute", errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateOnStart_FailsWhenProxyUrlIsInvalid()
    {
        var services = new ServiceCollection();
        services.AddOptions<MirrorOptions>()
            .Configure(o =>
            {
                o.Upstream.RegistryUrl = "https://registry-1.docker.io";
                o.Upstream.TokenRealm = "https://auth.docker.io/token";
                o.Upstream.TokenService = "registry.docker.io";
                o.Upstream.Proxy = new ProxyOptions { Url = "not-a-valid-url" };
            });
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsValidator>();
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsUpstreamValidator>();
        using ServiceProvider sp = services.BuildServiceProvider();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<IOptions<MirrorOptions>>().Value);

        Assert.Contains("not-a-valid-url", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateOnStart_SucceedsWhenNoProxyConfigured()
    {
        var services = new ServiceCollection();
        services.AddOptions<MirrorOptions>()
            .Configure(o =>
            {
                o.Upstream.RegistryUrl = "https://registry-1.docker.io";
                o.Upstream.TokenRealm = "https://auth.docker.io/token";
                o.Upstream.TokenService = "registry.docker.io";
            });
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsValidator>();
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsUpstreamValidator>();
        using ServiceProvider sp = services.BuildServiceProvider();

        MirrorOptions options = sp.GetRequiredService<IOptions<MirrorOptions>>().Value;
        Assert.NotNull(options);
    }

    [Fact]
    public void Validator_DirectCall_ReturnsFailed_ForInvalidProxy()
    {
        var options = new UpstreamOptions
        {
            RegistryUrl = "https://registry-1.docker.io",
            TokenRealm = "https://auth.docker.io/token",
            TokenService = "registry.docker.io",
            Proxy = new ProxyOptions { Url = "not-a-valid-url" },
        };

        ValidateOptionsResult result = new MirrorOptionsUpstreamValidator().Validate(null, new MirrorOptions { Upstream = options });

        Assert.True(result.Failed);
        Assert.Contains("not-a-valid-url", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validator_DirectCall_ReturnsSucceeded_ForValidProxy()
    {
        var options = new UpstreamOptions
        {
            RegistryUrl = "https://registry-1.docker.io",
            TokenRealm = "https://auth.docker.io/token",
            TokenService = "registry.docker.io",
            Proxy = new ProxyOptions { Url = "http://proxy.corp.example:8080" },
        };

        ValidateOptionsResult result = new MirrorOptionsUpstreamValidator().Validate(null, new MirrorOptions { Upstream = options });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validator_DirectCall_ReturnsSucceeded_WhenProxyIsNull()
    {
        var options = new UpstreamOptions
        {
            RegistryUrl = "https://registry-1.docker.io",
            TokenRealm = "https://auth.docker.io/token",
            TokenService = "registry.docker.io",
        };

        ValidateOptionsResult result = new MirrorOptionsUpstreamValidator().Validate(null, new MirrorOptions { Upstream = options });

        Assert.True(result.Succeeded);
    }

    private static void AddAllValidators(IServiceCollection services)
    {
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsValidator>();
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsUpstreamValidator>();
    }

    [Fact]
    public void UpstreamUrlsValidator_DirectCall_ReturnsFailed_ForInvalidRegistryUrl()
    {
        var options = new UpstreamOptions
        {
            RegistryUrl = "not-a-valid-url",
            TokenRealm = "https://auth.docker.io/token",
            TokenService = "registry.docker.io",
        };

        ValidateOptionsResult result = new MirrorOptionsUpstreamValidator().Validate(null, new MirrorOptions { Upstream = options });

        Assert.True(result.Failed);
        Assert.Contains("RegistryUrl", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not-a-valid-url", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UpstreamUrlsValidator_DirectCall_ReturnsFailed_ForInvalidTokenRealm()
    {
        var options = new UpstreamOptions
        {
            RegistryUrl = "https://registry-1.docker.io",
            TokenRealm = "not-a-valid-url",
            TokenService = "registry.docker.io",
        };

        ValidateOptionsResult result = new MirrorOptionsUpstreamValidator().Validate(null, new MirrorOptions { Upstream = options });

        Assert.True(result.Failed);
        Assert.Contains("TokenRealm", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not-a-valid-url", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UpstreamUrlsValidator_DirectCall_ReturnsFailed_ForEmptyRegistryUrl()
    {
        var options = new UpstreamOptions
        {
            RegistryUrl = string.Empty,
            TokenRealm = "https://auth.docker.io/token",
            TokenService = "registry.docker.io",
        };

        ValidateOptionsResult result = new MirrorOptionsUpstreamValidator().Validate(null, new MirrorOptions { Upstream = options });

        Assert.True(result.Failed);
        Assert.Contains("RegistryUrl", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UpstreamUrlsValidator_DirectCall_ReturnsSucceeded_ForValidUrls()
    {
        var options = new UpstreamOptions
        {
            RegistryUrl = "https://registry-1.docker.io",
            TokenRealm = "https://auth.docker.io/token",
            TokenService = "registry.docker.io",
        };

        ValidateOptionsResult result = new MirrorOptionsUpstreamValidator().Validate(null, new MirrorOptions { Upstream = options });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void UpstreamUrlsValidator_DirectCall_ReturnsMultipleErrors_WhenBothUrlsInvalid()
    {
        var options = new UpstreamOptions
        {
            RegistryUrl = "bad-registry",
            TokenRealm = "bad-realm",
            TokenService = "registry.docker.io",
        };

        ValidateOptionsResult result = new MirrorOptionsUpstreamValidator().Validate(null, new MirrorOptions { Upstream = options });

        Assert.True(result.Failed);
        Assert.Contains("RegistryUrl", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TokenRealm", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateOnStart_FailsWhenRegistryUrlIsInvalid()
    {
        var services = new ServiceCollection();
        services.AddOptions<MirrorOptions>()
            .Configure(o =>
            {
                o.Upstream.RegistryUrl = "not-a-valid-url";
                o.Upstream.TokenRealm = "https://auth.docker.io/token";
                o.Upstream.TokenService = "registry.docker.io";
            });
        AddAllValidators(services);
        using ServiceProvider sp = services.BuildServiceProvider();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<IOptions<MirrorOptions>>().Value);

        Assert.Contains("RegistryUrl", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateOnStart_FailsWhenTokenRealmIsInvalid()
    {
        var services = new ServiceCollection();
        services.AddOptions<MirrorOptions>()
            .Configure(o =>
            {
                o.Upstream.RegistryUrl = "https://registry-1.docker.io";
                o.Upstream.TokenRealm = "not-a-valid-url";
                o.Upstream.TokenService = "registry.docker.io";
            });
        AddAllValidators(services);
        using ServiceProvider sp = services.BuildServiceProvider();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<IOptions<MirrorOptions>>().Value);

        Assert.Contains("TokenRealm", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateOnStart_SucceedsWhenAllUrlsAreValid()
    {
        var services = new ServiceCollection();
        services.AddOptions<MirrorOptions>()
            .Configure(o =>
            {
                o.Upstream.RegistryUrl = "https://registry-1.docker.io";
                o.Upstream.TokenRealm = "https://auth.docker.io/token";
                o.Upstream.TokenService = "registry.docker.io";
            });
        AddAllValidators(services);
        using ServiceProvider sp = services.BuildServiceProvider();

        MirrorOptions options = sp.GetRequiredService<IOptions<MirrorOptions>>().Value;
        Assert.NotNull(options);
    }
}
