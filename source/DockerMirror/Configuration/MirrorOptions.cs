using Microsoft.Extensions.Options;

namespace DockerMirror.Configuration;

/// <summary>
/// Root options bound to the <c>Mirror</c> configuration section. Aggregates upstream,
/// cache, admin, and base-path settings. All consumers inject
/// <see cref="IOptions{MirrorOptions}"/> and read <see cref="Upstream"/>,
/// <see cref="Cache"/>, or <see cref="Admin"/> from the single bound snapshot.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="NormalizedBasePath"/> produces the URL prefix used for route grouping.
/// <see cref="NormalizeBasePath"/> is the public canonicalization helper — trimming
/// whitespace and trailing slashes, ensuring a leading <c>/</c>, and returning
/// <see cref="string.Empty"/> when no meaningful prefix is configured.
/// </para>
/// <para>
/// With <c>Mirror:BasePath = "/docker"</c> all <c>/v2/*</c> and admin endpoints are
/// served under <c>/docker/v2/...</c> and <c>/docker/admin/stats</c>.
/// <c>/health/live</c> and <c>/health/ready</c> always remain at root regardless of
/// <see cref="BasePath"/>.
/// </para>
/// </remarks>
public sealed class MirrorOptions
{
    public const string SectionName = "Mirror";

    /// <summary>
    /// Configuration for the upstream Docker registry.
    /// </summary>
    /// <remarks>Corresponds to the <c>Mirror:Upstream</c> configuration section.</remarks>
    [ValidateObjectMembers]
    public UpstreamOptions Upstream { get; set; } = new();

    /// <summary>
    /// Configuration for local caching of upstream content.
    /// </summary>
    /// <remarks>Corresponds to the <c>Mirror:Cache</c> configuration section.</remarks>
    [ValidateObjectMembers]
    public CacheOptions Cache { get; set; } = new();

    /// <summary>
    /// Configuration for the admin operational-stats endpoint.
    /// </summary>
    /// <remarks>Corresponds to the <c>Mirror:Admin</c> configuration section.</remarks>
    public AdminOptions Admin { get; set; } = new();

    /// <summary>
    /// Optional URL path prefix for all <c>/v2/*</c> and <c>/admin/stats</c>
    /// endpoints. <c>null</c> (the default) means routes are served at root.
    /// </summary>
    public string? BasePath { get; set; }

    public string NormalizedBasePath => NormalizeBasePath(BasePath);

    /// <summary>
    /// Canonicalizes <paramref name="value"/> into a URL-safe path prefix:
    /// trims whitespace and trailing slashes, ensures a single leading <c>/</c>,
    /// and returns <see cref="string.Empty"/> for empty/root values.
    /// </summary>
    public static string NormalizeBasePath(string? value)
    {
        string trimmed = (value ?? string.Empty).Trim();

        if (trimmed.Length == 0 || trimmed == "/")
        {
            return string.Empty;
        }

        trimmed = trimmed.TrimEnd('/');

        return "/" + trimmed.TrimStart('/');
    }
}
