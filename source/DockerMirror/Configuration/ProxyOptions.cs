namespace DockerMirror.Configuration;

public enum UpstreamEndpoint
{
    Registry,
    Auth,
    Cdn,
}

public sealed class ProxyOptions
{
    public string? Url { get; set; }

    public string? Registry { get; set; }

    public string? Auth { get; set; }

    public string? Cdn { get; set; }

    public string? Resolve(UpstreamEndpoint endpoint)
    {
        string? perEndpoint = endpoint switch
        {
            UpstreamEndpoint.Registry => Registry,
            UpstreamEndpoint.Auth => Auth,
            UpstreamEndpoint.Cdn => Cdn,
            _ => null,
        };

        return !string.IsNullOrEmpty(perEndpoint) ? perEndpoint : (string.IsNullOrEmpty(Url) ? null : Url);
    }

    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();

        Check("Url", Url, errors);
        Check("Registry", Registry, errors);
        Check("Auth", Auth, errors);
        Check("Cdn", Cdn, errors);

        return errors;
    }

    private static void Check(string name, string? value, List<string> errors)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not "http" and not "https")
        {
            errors.Add($"Mirror:Upstream:Proxy:{name} value '{value}' is not a valid absolute http/https URI.");
        }
    }
}
