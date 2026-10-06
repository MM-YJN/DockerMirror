using System.Diagnostics;
using System.Security.Cryptography;

namespace DockerMirror.Caching;

[DebuggerDisplay("{Algorithm}:{Hex}")]
internal readonly record struct Digest(string Algorithm, string Hex)
{
    public HashAlgorithmName AlgorithmName => Algorithm switch
    {
        "sha256" => HashAlgorithmName.SHA256,
        "sha512" => HashAlgorithmName.SHA512,
        _ => throw new InvalidOperationException($"Unsupported digest algorithm '{Algorithm}'."),
    };

    public string Canonical => $"{Algorithm}:{Hex}";

    public override string ToString() => Canonical;

    public static bool IsDigest(string? reference)
    {
        if (reference is null)
        {
            return false;
        }

        int colon = reference.IndexOf(':');
        if (colon < 0)
        {
            return false;
        }

        ReadOnlySpan<char> algo = reference.AsSpan(0, colon);
        ReadOnlySpan<char> hex = reference.AsSpan(colon + 1);

        if (algo.Equals("sha256", StringComparison.Ordinal))
        {
            return hex.Length == 64 && IsHex(hex);
        }

        if (algo.Equals("sha512", StringComparison.Ordinal))
        {
            return hex.Length == 128 && IsHex(hex);
        }

        return false;
    }

    public static bool TryParse(string? reference, out Digest digest)
    {
        if (reference is null)
        {
            digest = default;
            return false;
        }

        int colon = reference.IndexOf(':');
        if (colon < 0)
        {
            digest = default;
            return false;
        }

        ReadOnlySpan<char> algo = reference.AsSpan(0, colon);
        ReadOnlySpan<char> hex = reference.AsSpan(colon + 1);

        if (algo.Equals("sha256", StringComparison.Ordinal) && hex.Length == 64 && IsHex(hex))
        {
            string lower = new string(hex).ToLowerInvariant();
            digest = new Digest("sha256", lower);
            return true;
        }

        if (algo.Equals("sha512", StringComparison.Ordinal) && hex.Length == 128 && IsHex(hex))
        {
            string lower = new string(hex).ToLowerInvariant();
            digest = new Digest("sha512", lower);
            return true;
        }

        digest = default;
        return false;
    }

    public static Digest Parse(string reference)
    {
        if (!TryParse(reference, out Digest digest))
        {
            throw new FormatException($"'{reference}' is not a valid content digest.");
        }

        return digest;
    }

    private static bool IsHex(ReadOnlySpan<char> span)
    {
        foreach (char c in span)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
