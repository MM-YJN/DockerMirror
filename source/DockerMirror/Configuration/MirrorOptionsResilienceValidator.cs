using Microsoft.Extensions.Options;

namespace DockerMirror.Configuration;

internal sealed class MirrorOptionsResilienceValidator : IValidateOptions<MirrorOptions>
{
    public ValidateOptionsResult Validate(string? name, MirrorOptions options)
    {
        var errors = new List<string>();

        CheckNonNegative("Mirror:Upstream:TokenCacheTtl", options.Upstream.TokenCacheTtl, errors);

        UpstreamResilienceOptions r = options.Upstream.Resilience;
        CheckNonNegative("Mirror:Upstream:Resilience:BaseDelay", r.BaseDelay, errors);
        CheckNonNegative("Mirror:Upstream:Resilience:AttemptTimeout", r.AttemptTimeout, errors);
        CheckNonNegative("Mirror:Upstream:Resilience:TotalRequestTimeout", r.TotalRequestTimeout, errors);
        CheckNonNegative("Mirror:Upstream:Resilience:HeadersTimeout", r.HeadersTimeout, errors);
        CheckNonNegative("Mirror:Upstream:Resilience:SamplingDuration", r.SamplingDuration, errors);
        CheckNonNegative("Mirror:Upstream:Resilience:BreakDuration", r.BreakDuration, errors);
        CheckNonNegative("Mirror:Upstream:Resilience:ConnectTimeout", r.ConnectTimeout, errors);

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
}
