using System.Net;

using DockerMirror.Configuration;
using DockerMirror.Registry;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DockerMirror.UnitTests.Registry;

public sealed class UpstreamProxyTests
{
    [Fact]
    public void CreatePrimaryHandler_NoProxyConfigured_LeavesProxyNull()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new MirrorOptions
        {
            Upstream = new UpstreamOptions
            {
                RegistryUrl = "https://registry-1.docker.io",
                TokenRealm = "https://auth.docker.io/token",
                TokenService = "registry.docker.io",
            }
        }));
        using ServiceProvider sp = services.BuildServiceProvider();

        SocketsHttpHandler handler = UpstreamResilienceExtensions.CreatePrimaryHandler(sp, allowAutoRedirect: false, UpstreamEndpoint.Registry);

        Assert.Null(handler.Proxy);
    }

    [Fact]
    public void CreatePrimaryHandler_GlobalUrlOnly_AppliesProxyToAllEndpoints()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new MirrorOptions
        {
            Upstream = new UpstreamOptions
            {
                RegistryUrl = "https://registry-1.docker.io",
                TokenRealm = "https://auth.docker.io/token",
                TokenService = "registry.docker.io",
                Proxy = new ProxyOptions { Url = "http://proxy.corp.example:8080" },
            }
        }));
        using ServiceProvider sp = services.BuildServiceProvider();

        SocketsHttpHandler registryHandler = UpstreamResilienceExtensions.CreatePrimaryHandler(sp, allowAutoRedirect: false, UpstreamEndpoint.Registry);
        SocketsHttpHandler authHandler = UpstreamResilienceExtensions.CreatePrimaryHandler(sp, allowAutoRedirect: true, UpstreamEndpoint.Auth);
        SocketsHttpHandler cdnHandler = UpstreamResilienceExtensions.CreatePrimaryHandler(sp, allowAutoRedirect: true, UpstreamEndpoint.Cdn);

        Assert.NotNull(registryHandler.Proxy);
        Assert.True(registryHandler.UseProxy);
        Assert.Equal("http://proxy.corp.example:8080/", registryHandler.Proxy!.GetProxy(new Uri("https://registry-1.docker.io"))!.AbsoluteUri);

        Assert.NotNull(authHandler.Proxy);
        Assert.True(authHandler.UseProxy);
        Assert.Equal("http://proxy.corp.example:8080/", authHandler.Proxy!.GetProxy(new Uri("https://auth.docker.io"))!.AbsoluteUri);

        Assert.NotNull(cdnHandler.Proxy);
        Assert.True(cdnHandler.UseProxy);
        Assert.Equal("http://proxy.corp.example:8080/", cdnHandler.Proxy!.GetProxy(new Uri("https://cdn.docker.com"))!.AbsoluteUri);
    }

    [Fact]
    public void CreatePrimaryHandler_PerEndpointOverride_TakesPrecedence()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new MirrorOptions
        {
            Upstream = new UpstreamOptions
            {
                RegistryUrl = "https://registry-1.docker.io",
                TokenRealm = "https://auth.docker.io/token",
                TokenService = "registry.docker.io",
                Proxy = new ProxyOptions
                {
                    Url = "http://proxy.corp.example:8080",
                    Registry = "http://registry-proxy.corp.example:3128",
                },
            }
        }));
        using ServiceProvider sp = services.BuildServiceProvider();

        SocketsHttpHandler registryHandler = UpstreamResilienceExtensions.CreatePrimaryHandler(sp, allowAutoRedirect: false, UpstreamEndpoint.Registry);
        SocketsHttpHandler authHandler = UpstreamResilienceExtensions.CreatePrimaryHandler(sp, allowAutoRedirect: true, UpstreamEndpoint.Auth);

        Assert.Equal("http://registry-proxy.corp.example:3128/", registryHandler.Proxy!.GetProxy(new Uri("https://registry-1.docker.io"))!.AbsoluteUri);
        Assert.Equal("http://proxy.corp.example:8080/", authHandler.Proxy!.GetProxy(new Uri("https://auth.docker.io"))!.AbsoluteUri);
    }

    [Fact]
    public void CreatePrimaryHandler_PerEndpointOverrideWithoutGlobal_OnlyAppliesToThatEndpoint()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new MirrorOptions
        {
            Upstream = new UpstreamOptions
            {
                RegistryUrl = "https://registry-1.docker.io",
                TokenRealm = "https://auth.docker.io/token",
                TokenService = "registry.docker.io",
                Proxy = new ProxyOptions
                {
                    Registry = "http://registry-proxy.corp.example:3128",
                },
            }
        }));
        using ServiceProvider sp = services.BuildServiceProvider();

        SocketsHttpHandler registryHandler = UpstreamResilienceExtensions.CreatePrimaryHandler(sp, allowAutoRedirect: false, UpstreamEndpoint.Registry);
        SocketsHttpHandler authHandler = UpstreamResilienceExtensions.CreatePrimaryHandler(sp, allowAutoRedirect: true, UpstreamEndpoint.Auth);

        Assert.NotNull(registryHandler.Proxy);
        Assert.True(registryHandler.UseProxy);
        Assert.Equal("http://registry-proxy.corp.example:3128/", registryHandler.Proxy!.GetProxy(new Uri("https://registry-1.docker.io"))!.AbsoluteUri);

        Assert.Null(authHandler.Proxy);
    }

    [Fact]
    public void CreatePrimaryHandler_ProxyUrlWithCredentials_ExtractsAndSetsCredentials()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new MirrorOptions
        {
            Upstream = new UpstreamOptions
            {
                RegistryUrl = "https://registry-1.docker.io",
                TokenRealm = "https://auth.docker.io/token",
                TokenService = "registry.docker.io",
                Proxy = new ProxyOptions { Url = "http://user:pass@proxy.corp.example:8080" },
            }
        }));
        using ServiceProvider sp = services.BuildServiceProvider();

        SocketsHttpHandler handler = UpstreamResilienceExtensions.CreatePrimaryHandler(sp, allowAutoRedirect: false, UpstreamEndpoint.Registry);

        Assert.NotNull(handler.Proxy);
        NetworkCredential credentials = Assert.IsType<NetworkCredential>(handler.Proxy!.Credentials);
        Assert.Equal("user", credentials.UserName);
        Assert.Equal("pass", credentials.Password);
    }

    [Fact]
    public void CreatePrimaryHandler_ProxyUrlWithoutCredentials_LeavesCredentialsNull()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new MirrorOptions
        {
            Upstream = new UpstreamOptions
            {
                RegistryUrl = "https://registry-1.docker.io",
                TokenRealm = "https://auth.docker.io/token",
                TokenService = "registry.docker.io",
                Proxy = new ProxyOptions { Url = "http://proxy.corp.example:8080" },
            }
        }));
        using ServiceProvider sp = services.BuildServiceProvider();

        SocketsHttpHandler handler = UpstreamResilienceExtensions.CreatePrimaryHandler(sp, allowAutoRedirect: false, UpstreamEndpoint.Registry);

        Assert.NotNull(handler.Proxy);
        Assert.Null(handler.Proxy!.Credentials);
    }

    [Fact]
    public void CreatePrimaryHandler_ProxyUrlWithCredentialsInPerEndpointOverride_ExtractsCredentials()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new MirrorOptions
        {
            Upstream = new UpstreamOptions
            {
                RegistryUrl = "https://registry-1.docker.io",
                TokenRealm = "https://auth.docker.io/token",
                TokenService = "registry.docker.io",
                Proxy = new ProxyOptions
                {
                    Url = "http://global:gpass@proxy.corp.example:8080",
                    Registry = "http://reguser:regpass@registry-proxy.corp.example:3128",
                },
            }
        }));
        using ServiceProvider sp = services.BuildServiceProvider();

        SocketsHttpHandler registryHandler = UpstreamResilienceExtensions.CreatePrimaryHandler(sp, allowAutoRedirect: false, UpstreamEndpoint.Registry);
        SocketsHttpHandler authHandler = UpstreamResilienceExtensions.CreatePrimaryHandler(sp, allowAutoRedirect: true, UpstreamEndpoint.Auth);

        NetworkCredential regCreds = Assert.IsType<NetworkCredential>(registryHandler.Proxy!.Credentials);
        Assert.Equal("reguser", regCreds.UserName);
        Assert.Equal("regpass", regCreds.Password);

        NetworkCredential authCreds = Assert.IsType<NetworkCredential>(authHandler.Proxy!.Credentials);
        Assert.Equal("global", authCreds.UserName);
        Assert.Equal("gpass", authCreds.Password);
    }
}
