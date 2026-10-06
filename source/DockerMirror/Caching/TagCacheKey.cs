using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DockerMirror.Caching;

internal static class TagCacheKey
{
    public static string From(string rewrittenName, string tag, string? acceptKey)
    {
        string normalizedAccept = NormalizeAccept(acceptKey);

        string input = $"{rewrittenName}\n{tag}\n{normalizedAccept}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        string hex = Convert.ToHexStringLower(hash);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"tags/{hex.AsSpan(0, 2)}/{hex.AsSpan(2, 2)}/{hex}");
    }

    internal static string NormalizeAccept(string? accept)
    {
        if (string.IsNullOrWhiteSpace(accept))
        {
            return string.Empty;
        }

        var parts = new List<string>();

        foreach (string part in accept.Split(','))
        {
            string trimmed = part.Trim();
            if (trimmed.Length > 0)
            {
                parts.Add(trimmed.ToLowerInvariant());
            }
        }

        parts.Sort(StringComparer.Ordinal);

        return string.Join(',', parts);
    }
}
