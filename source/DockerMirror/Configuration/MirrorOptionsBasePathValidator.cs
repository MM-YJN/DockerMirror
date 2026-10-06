using System.Buffers;

using Microsoft.Extensions.Options;

namespace DockerMirror.Configuration;

internal sealed class MirrorOptionsBasePathValidator : IValidateOptions<MirrorOptions>
{
    private static readonly SearchValues<char> s_disallowedChars = SearchValues.Create(['?', '#', ':', '{', '}', '*']);

    public ValidateOptionsResult Validate(string? name, MirrorOptions options)
    {
        string? raw = options.BasePath;
        string? value = raw?.Trim();

        if (string.IsNullOrEmpty(value))
        {
            return ValidateOptionsResult.Success;
        }

        if (value.Contains("..", StringComparison.Ordinal))
        {
            return ValidateOptionsResult.Fail(
                "Mirror:BasePath value must not contain '..'.");
        }

        if (value.Contains("//", StringComparison.Ordinal))
        {
            return ValidateOptionsResult.Fail(
                "Mirror:BasePath value must not contain '//'.");
        }

        int disallowedIndex = value.AsSpan().IndexOfAny(s_disallowedChars);
        if (disallowedIndex >= 0)
        {
            return ValidateOptionsResult.Fail(
                $"Mirror:BasePath value must not contain '{value[disallowedIndex]}'.");
        }

        for (int i = 0; i < value.Length; i++)
        {
            if (char.IsWhiteSpace(value[i]))
            {
                return ValidateOptionsResult.Fail(
                    "Mirror:BasePath value must not contain internal whitespace.");
            }
        }

        return ValidateOptionsResult.Success;
    }
}
