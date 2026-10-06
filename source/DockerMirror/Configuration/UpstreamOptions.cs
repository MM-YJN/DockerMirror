using Microsoft.Extensions.Options;

namespace DockerMirror.Configuration;

public sealed class UpstreamOptions
{
    /// <summary>
    /// URL of the upstream Docker registry. Defaults to
    /// <c>https://registry-1.docker.io</c>.
    /// </summary>
    public string RegistryUrl { get; set; } = "https://registry-1.docker.io";

    /// <summary>
    /// URL of the upstream token-authentication realm. Defaults to
    /// <c>https://auth.docker.io/token</c>.
    /// </summary>
    public string TokenRealm { get; set; } = "https://auth.docker.io/token";

    /// <summary>
    /// Token service identifier sent to the upstream auth realm. Defaults to
    /// <c>registry.docker.io</c>.
    /// </summary>
    public string TokenService { get; set; } = "registry.docker.io";

    public string DefaultNamespace { get; set; } = "library";

    public TimeSpan TokenCacheTtl { get; set; } = TimeSpan.FromMinutes(5);

    public UpstreamAuthOptions? Auth { get; set; }

    public ProxyOptions? Proxy { get; set; }

    [ValidateObjectMembers]
    public UpstreamResilienceOptions Resilience { get; set; } = new();
}

public sealed class UpstreamAuthOptions
{
    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;
}
