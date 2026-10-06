using Microsoft.Extensions.Options;

namespace DockerMirror.Configuration;

internal sealed class MirrorOptionsS3Validator : IValidateOptions<MirrorOptions>
{
    public ValidateOptionsResult Validate(string? name, MirrorOptions options)
    {
        if (!string.Equals(options.Cache.Backend, "S3", StringComparison.OrdinalIgnoreCase))
        {
            return ValidateOptionsResult.Skip;
        }

        var errors = new List<string>();

        if (string.IsNullOrEmpty(options.Cache.S3.Bucket))
        {
            errors.Add("Mirror:Cache:S3:Bucket is required when Backend is 'S3'.");
        }

        if (string.IsNullOrEmpty(options.Cache.S3.AccessKey))
        {
            errors.Add("Mirror:Cache:S3:AccessKey is required when Backend is 'S3'.");
        }

        if (string.IsNullOrEmpty(options.Cache.S3.SecretKey))
        {
            errors.Add("Mirror:Cache:S3:SecretKey is required when Backend is 'S3'.");
        }

        if (string.IsNullOrEmpty(options.Cache.S3.Region))
        {
            errors.Add("Mirror:Cache:S3:Region is required when Backend is 'S3'.");
        }

        string? serviceUrl = options.Cache.S3.ServiceUrl;
        if (!string.IsNullOrEmpty(serviceUrl)
            && (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out Uri? uri)
                || uri.Scheme is not "http" and not "https"))
        {
            errors.Add($"Mirror:Cache:S3:ServiceUrl value '{serviceUrl}' is not a valid absolute http/https URI.");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
