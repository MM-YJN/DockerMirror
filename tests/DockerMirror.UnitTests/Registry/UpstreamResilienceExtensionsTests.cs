using System.Net;

using DockerMirror.Configuration;
using DockerMirror.Registry;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DockerMirror.UnitTests.Registry;

public sealed class UpstreamResilienceExtensionsTests
{
    // Regression guard for the persistent blob cache-miss bug: every upstream-facing
    // handler must auto-decompress transport-level Content-Encoding so the cached blob
    // hash is computed over the actual (decompressed) OCI content and matches the digest.
    [Theory]
    [InlineData(UpstreamEndpoint.Registry, false)]
    [InlineData(UpstreamEndpoint.Auth, true)]
    [InlineData(UpstreamEndpoint.Cdn, true)]
    public void CreatePrimaryHandler_EnablesAutomaticDecompressionForAllEndpoints(
        UpstreamEndpoint endpoint, bool allowAutoRedirect)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new MirrorOptions
        {
            Upstream = new UpstreamOptions
            {
                RegistryUrl = "https://registry-1.docker.io",
                TokenRealm = "https://auth.docker.io/token",
                TokenService = "registry.docker.io",
            },
        }));
        using ServiceProvider sp = services.BuildServiceProvider();

        SocketsHttpHandler handler = UpstreamResilienceExtensions.CreatePrimaryHandler(sp, allowAutoRedirect, endpoint);

        Assert.Equal(DecompressionMethods.All, handler.AutomaticDecompression);
    }
}
