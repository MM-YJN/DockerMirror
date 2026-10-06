using Microsoft.Extensions.Options;

namespace DockerMirror.Configuration;

internal sealed class MirrorOptionsUpstreamValidator : IValidateOptions<MirrorOptions>
{
    public ValidateOptionsResult Validate(string? name, MirrorOptions options)
    {
        UpstreamOptions upstream = options.Upstream;
        var errors = new List<string>();

        CheckUrl("RegistryUrl", upstream.RegistryUrl, errors);
        CheckUrl("TokenRealm", upstream.TokenRealm, errors);

        IReadOnlyList<string> proxyErrors = upstream.Proxy?.GetValidationErrors() ?? [];
        errors.AddRange(proxyErrors);

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }

    private static void CheckUrl(string name, string value, List<string> errors)
    {
        if (string.IsNullOrEmpty(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not "http" and not "https")
        {
            errors.Add($"Mirror:Upstream:{name} value '{value}' is not a valid absolute http/https URI.");
        }
    }
}
