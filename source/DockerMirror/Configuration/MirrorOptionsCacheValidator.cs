using Microsoft.Extensions.Options;

namespace DockerMirror.Configuration;

internal sealed class MirrorOptionsCacheValidator : IValidateOptions<MirrorOptions>
{
    private static readonly string[] s_allowedBackends = ["FileSystem", "S3"];

    public ValidateOptionsResult Validate(string? name, MirrorOptions options)
    {
        var errors = new List<string>();

        string backend = options.Cache.Backend;
        if (string.IsNullOrEmpty(backend)
            || !s_allowedBackends.Contains(backend, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"Mirror:Cache:Backend value '{backend}' must be one of: {string.Join(", ", s_allowedBackends)}.");
        }

        if (options.Cache.WarmQueueCapacity < 1)
        {
            errors.Add($"Mirror:Cache:WarmQueueCapacity must be >= 1. Current value: {options.Cache.WarmQueueCapacity}.");
        }

        string? dir = options.Cache.FileSystem.Directory?.Trim();
        if (string.IsNullOrEmpty(dir))
        {
            errors.Add("Mirror:Cache:FileSystem:Directory must be a non-empty path.");
        }
        else
        {
            string[] segments = dir.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            if (Array.Exists(segments, s => s == ".."))
            {
                errors.Add($"Mirror:Cache:FileSystem:Directory value '{dir}' must not contain '..'.");
            }
        }

        CheckNonNegative("Mirror:Cache:Eviction:Interval", options.Cache.Eviction.Interval, errors);

        if (options.Cache.Eviction.MaxSizeBytes is long maxSize)
        {
            if (maxSize < 1)
            {
                errors.Add($"Mirror:Cache:Eviction:MaxSizeBytes must be >= 1 when set. Current value: {maxSize}.");
            }
        }

        if (options.Cache.Eviction.MaxAge is TimeSpan maxAge && maxAge < TimeSpan.Zero)
        {
            errors.Add($"Mirror:Cache:Eviction:MaxAge must be >= 0 when set. Current value: {maxAge}.");
        }

        CheckNonNegative("Mirror:Cache:SizeReporting:Interval", options.Cache.SizeReporting.Interval, errors);
        CheckNonNegative("Mirror:Cache:TagManifests:Ttl", options.Cache.TagManifests.Ttl, errors);
        CheckNonNegative("Mirror:Cache:NegativeCache:Ttl", options.Cache.NegativeCache.Ttl, errors);
        CheckNonNegative("Mirror:Cache:Headers:ImmutableMaxAge", options.Cache.Headers.ImmutableMaxAge, errors);

        ValidateListCacheOptions("Mirror:Cache:TagsList", options.Cache.TagsList, errors);
        ValidateListCacheOptions("Mirror:Cache:Catalog", options.Cache.Catalog, errors);

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }

    private static void CheckNonNegative(string key, TimeSpan value, List<string> errors)
    {
        if (value < TimeSpan.Zero)
        {
            errors.Add($"{key} must be a non-negative TimeSpan. Current value: {value}.");
        }
    }

    private static void ValidateListCacheOptions(string prefix, ListCacheOptions options, List<string> errors)
    {
        CheckNonNegative($"{prefix}:Ttl", options.Ttl, errors);

        if (options.MaxEntries < 1)
        {
            errors.Add($"{prefix}:MaxEntries must be >= 1. Current value: {options.MaxEntries}.");
        }

        if (options.MaxBodyBytes < 1)
        {
            errors.Add($"{prefix}:MaxBodyBytes must be >= 1. Current value: {options.MaxBodyBytes}.");
        }
    }
}
