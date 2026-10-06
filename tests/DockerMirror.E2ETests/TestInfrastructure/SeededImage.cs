namespace DockerMirror.E2ETests.TestInfrastructure;

public sealed record SeededImage
{
    public required string Repository { get; init; }

    public required string Tag { get; init; }

    public required string ConfigDigest { get; init; }

    public required long ConfigSize { get; init; }

    public required string LayerDigest { get; init; }

    public required long LayerSize { get; init; }

    public required string ManifestDigest { get; init; }

    public required long ManifestSize { get; init; }

    public string LayerBlobPath => $"/v2/{Repository}/blobs/{LayerDigest}";

    public string Reference => $"{Repository}:{Tag}";
}
