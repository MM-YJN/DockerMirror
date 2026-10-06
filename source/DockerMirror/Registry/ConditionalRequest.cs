using System.Globalization;

using DockerMirror.Caching;

using Microsoft.Extensions.Primitives;

namespace DockerMirror.Registry;

// Static helper for HTTP conditional request processing (ETag, If-None-Match, Cache-Control, Age).
// Follows RFC 7232: uses weak comparison for If-None-Match (§3.2), generates strong ETags from
// content digests, and emits Age from StoredAtUtc when available.
internal static class ConditionalRequest
{
    internal static string ToETag(Digest digest) => $"\"{digest.Canonical}\"";

    internal static string FormatETag(string digestCanonical) => $"\"{digestCanonical}\"";

    // Returns true when the client's If-None-Match header contains a token that weak-matches
    // the given etag (RFC 7232 §3.2). Returns false for absent header and for the "*" token
    // (see design decision E — we never early-304 on "*").
    internal static bool IfNoneMatchMatches(HttpRequest request, string etag)
    {
        StringValues headerValues = request.Headers.IfNoneMatch;
        if (headerValues.Count == 0)
        {
            return false;
        }

        foreach (string? headerValue in headerValues)
        {
            if (headerValue is null)
            {
                continue;
            }

            foreach (string token in headerValue.Split(','))
            {
                ReadOnlySpan<char> trimmed = token.AsSpan().Trim();
                if (trimmed.IsEmpty)
                {
                    continue;
                }

                // Bare "*": conservative — we never match, per design decision E.
                if (trimmed.Length == 1 && trimmed[0] == '*')
                {
                    continue;
                }

                if (WeakEquals(trimmed, etag))
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static void ApplyValidatorHeaders(
        HttpResponse response,
        string? etag,
        string digestCanonical,
        string cacheControl,
        DateTimeOffset? storedAtUtc,
        TimeProvider timeProvider,
        bool setDockerContentDigest = true)
    {
        if (etag is not null)
        {
            response.Headers.ETag = etag;
        }

        if (setDockerContentDigest)
        {
            response.Headers[RegistryResponses.DockerContentDigest] = digestCanonical;
        }

        response.Headers.CacheControl = cacheControl;

        if (storedAtUtc is { } stored)
        {
            long age = (long)(timeProvider.GetUtcNow() - stored).TotalSeconds;
            if (age < 0)
            {
                age = 0;
            }

            response.Headers.Age = age.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            response.Headers.Age = "0";
        }
    }

    // Weak ETag comparison: strips optional "W/" prefix and surrounding double quotes,
    // then performs ordinal comparison against the given etag.
    private static bool WeakEquals(ReadOnlySpan<char> token, string etag)
    {
        ReadOnlySpan<char> normalized = token;

        if (normalized.StartsWith("W/", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        if (normalized.Length >= 2 && normalized[0] == '"' && normalized[^1] == '"')
        {
            normalized = normalized[1..^1];
        }

        return normalized.Equals(etag.AsSpan().Trim('"'), StringComparison.Ordinal);
    }
}
