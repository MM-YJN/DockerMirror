using System.Diagnostics;

namespace DockerMirror.Registry;

[DebuggerDisplay("{Name}/{ResourceType}/{Reference}")]
internal readonly record struct RegistryPath(string Name, string ResourceType, string? Reference)
{
    public string RewrittenName(string defaultNamespace) => Name.Contains('/') ? Name : $"{defaultNamespace}/{Name}";

    public string Scope => $"repository:{Name}:pull";

    public static RegistryPath Parse(ReadOnlySpan<char> path)
    {
        if (path.Length == 0)
        {
            throw new ArgumentException("Path cannot be empty", nameof(path));
        }

        int nameEnd = 0;

        while (nameEnd < path.Length)
        {
            int slash = path[nameEnd..].IndexOf('/');
            int segEnd = slash < 0 ? path.Length : nameEnd + slash;

            ReadOnlySpan<char> segment = path[nameEnd..segEnd];
            ReadOnlySpan<char> rest = segEnd < path.Length ? path[(segEnd + 1)..] : [];

            if (TryMatchResourceType(segment, rest, out string? resourceType, out string? reference))
            {
                string name = path[..nameEnd].TrimEnd('/').ToString();
                if (name.Length == 0)
                {
                    throw new InvalidOperationException($"Could not extract repository name from path: {path}");
                }

                return new RegistryPath(name, resourceType, reference);
            }

            nameEnd = segEnd + 1; // skip past the slash
        }

        throw new InvalidOperationException($"Could not parse registry path: {path}");
    }

    private static bool TryMatchResourceType(
        ReadOnlySpan<char> segment,
        ReadOnlySpan<char> rest,
        out string resourceType,
        out string? reference)
    {
        if (segment.Equals("manifests", StringComparison.Ordinal))
        {
            resourceType = "manifests";
            reference = ExtractNextSegment(rest);
            return true;
        }

        if (segment.Equals("tags", StringComparison.Ordinal))
        {
            resourceType = "tags";
            reference = ExtractNextSegment(rest);
            return true;
        }

        if (segment.Equals("referrers", StringComparison.Ordinal))
        {
            resourceType = "referrers";
            reference = ExtractNextSegment(rest);
            return true;
        }

        if (segment.Equals("blobs", StringComparison.Ordinal))
        {
            if (rest.Length > 0)
            {
                int nextSlash = rest.IndexOf('/');
                ReadOnlySpan<char> afterBlobs = nextSlash < 0 ? rest : rest[..nextSlash];
                if (afterBlobs.Equals("uploads", StringComparison.Ordinal))
                {
                    resourceType = "blobs/uploads";
                    ReadOnlySpan<char> afterUploads = nextSlash < 0
                        ? []
                        : rest[(nextSlash + 1)..];
                    reference = ExtractNextSegment(afterUploads);
                    return true;
                }
            }

            resourceType = "blobs";
            reference = ExtractNextSegment(rest);
            return true;
        }

        resourceType = string.Empty;
        reference = null;
        return false;
    }

    private static string? ExtractNextSegment(ReadOnlySpan<char> rest)
    {
        if (rest.IsEmpty)
        {
            return null;
        }

        int slash = rest.IndexOf('/');
        ReadOnlySpan<char> seg = slash < 0 ? rest : rest[..slash];
        return seg.ToString();
    }
}
