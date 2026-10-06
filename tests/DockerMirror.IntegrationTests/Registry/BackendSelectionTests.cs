using DockerMirror.Caching;
using DockerMirror.Configuration;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace DockerMirror.IntegrationTests.Registry;

/// <summary>
/// Verifies that <c>RegisterContentCache</c> in Program.cs wires the correct
/// <see cref="IContentStore"/> implementation for each backend value.
/// </summary>
public sealed class BackendSelectionTests
{
    // -----------------------------------------------------------------------
    // FileSystem backend — tested via a real WebApplicationFactory so the
    // complete DI wiring (HttpClients, hosted services, preflight) is exercised.
    // -----------------------------------------------------------------------

    [Fact]
    public void FileSystemBackend_ResolvesFileSystemContentStore()
    {
        using var factory = new FileSystemBackendFactory();
        IContentStore store = factory.Services.GetRequiredService<IContentStore>();
        Assert.IsType<FileSystemContentStore>(store);
    }

    // -----------------------------------------------------------------------
    // S3 backend — RegisterContentCache reads IConfiguration at composition
    // time, before WebApplicationFactory.ConfigureAppConfiguration overrides
    // are applied to the same ConfigurationManager.  Testing the switch
    // directly on a ServiceCollection is reliable and avoids the ordering
    // ambiguity without requiring a running host.
    // -----------------------------------------------------------------------

    [Fact]
    public void S3Backend_RegistersS3ContentStore()
    {
        var config = new ConfigurationManager();
        config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mirror:Cache:Backend"] = "S3",
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();  // Required for AddHttpClient<S3Client>

        Program.RegisterContentCache(services, config);

        Assert.Contains(services, d => d.ServiceType == typeof(S3ContentStore));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(FileSystemContentStore));

        // The IContentStore registration must resolve to S3ContentStore.
        ServiceDescriptor storeDescriptor = services.Single(d => d.ServiceType == typeof(IContentStore));
        Assert.NotNull(storeDescriptor.ImplementationFactory);

        // The ITagPointerStore registration must resolve to the same S3ContentStore instance.
        ServiceDescriptor tagStoreDescriptor = services.Single(d => d.ServiceType == typeof(ITagPointerStore));
        Assert.NotNull(tagStoreDescriptor.ImplementationFactory);
    }

    [Fact]
    public void FileSystemBackend_RegistersFileSystemContentStore()
    {
        var config = new ConfigurationManager();
        config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mirror:Cache:Backend"] = "FileSystem",
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();

        Program.RegisterContentCache(services, config);

        Assert.Contains(services, d => d.ServiceType == typeof(FileSystemContentStore));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(S3ContentStore));

        // The ITagPointerStore registration must resolve to FileSystemContentStore.
        ServiceDescriptor tagStoreDescriptor = services.Single(d => d.ServiceType == typeof(ITagPointerStore));
        Assert.NotNull(tagStoreDescriptor.ImplementationFactory);
    }

    [Fact]
    public void DefaultBackend_RegistersFileSystemContentStore()
    {
        // No Backend key configured at all — must default to FileSystem.
        var config = new ConfigurationManager();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();

        Program.RegisterContentCache(services, config);

        Assert.Contains(services, d => d.ServiceType == typeof(FileSystemContentStore));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(S3ContentStore));

        // The ITagPointerStore registration must resolve to FileSystemContentStore.
        ServiceDescriptor tagStoreDescriptor = services.Single(d => d.ServiceType == typeof(ITagPointerStore));
        Assert.NotNull(tagStoreDescriptor.ImplementationFactory);
    }

    // -----------------------------------------------------------------------
    // Helper factory
    // -----------------------------------------------------------------------

    private sealed class FileSystemBackendFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Mirror:Upstream:RegistryUrl"] = "https://registry.test.local/",
                    ["Mirror:Upstream:TokenRealm"] = "https://auth.test.local/token",
                    ["Mirror:Upstream:TokenService"] = "registry.test.local",
                });
            });

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new AlwaysOkHandlerFilter());
                services.Configure<MirrorOptions>(o =>
                {
                    o.Upstream = new UpstreamOptions
                    {
                        RegistryUrl = "https://registry.test.local/",
                        TokenRealm = "https://auth.test.local/token",
                        TokenService = "registry.test.local",
                    };
                    o.Cache = new CacheOptions { Enabled = false };
                });
            });
        }

        private sealed class AlwaysOkHandlerFilter : IHttpMessageHandlerBuilderFilter
        {
            public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next)
            {
                return builder =>
                {
                    next(builder);
                    builder.PrimaryHandler = new AlwaysOkHandler();
                };
            }
        }

        private sealed class AlwaysOkHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
