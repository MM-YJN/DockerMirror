using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DockerMirror.E2ETests.TestInfrastructure;

internal static class RegistryImageFactory
{
    private static readonly byte[] s_helloContent = "hello"u8.ToArray();

    public static ImageBuildResult Build(string repository, string tag)
    {
        // Layer: tar + gzip.
        byte[] uncompressedTar = BuildTar();
        string diffId = ComputeDigest(uncompressedTar);

        byte[] gzipBytes = GzipCompress(uncompressedTar);
        string layerDigest = ComputeDigest(gzipBytes);

        // Config.
        byte[] configJson = BuildConfigJson(diffId);
        string configDigest = ComputeDigest(configJson);

        // Manifest.
        byte[] manifestJson = BuildManifestJson(configDigest, configJson.Length, layerDigest, gzipBytes.Length);
        string manifestDigest = ComputeDigest(manifestJson);

        return new ImageBuildResult(
            new SeededImage
            {
                Repository = repository,
                Tag = tag,
                ConfigDigest = configDigest,
                ConfigSize = configJson.Length,
                LayerDigest = layerDigest,
                LayerSize = gzipBytes.Length,
                ManifestDigest = manifestDigest,
                ManifestSize = manifestJson.Length,
            },
            gzipBytes,
            configJson,
            manifestJson);
    }

    private static byte[] BuildTar()
    {
        using var ms = new MemoryStream();
        using (var writer = new TarWriter(ms, leaveOpen: true))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "hello.txt")
            {
                DataStream = new MemoryStream(s_helloContent),
            };
            writer.WriteEntry(entry);
        }

        return ms.ToArray();
    }

    private static byte[] GzipCompress(byte[] data)
    {
        using var outStream = new MemoryStream();
        using (var gzip = new GZipStream(outStream, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(data);
        }

        return outStream.ToArray();
    }

    private static byte[] BuildConfigJson(string diffId)
    {
        var config = new Dictionary<string, object>
        {
            ["architecture"] = "amd64",
            ["os"] = "linux",
            ["rootfs"] = new Dictionary<string, object>
            {
                ["type"] = "layers",
                ["diff_ids"] = new[] { diffId },
            },
            ["config"] = new Dictionary<string, object>
            {
                ["Cmd"] = new[] { "/bin/sh" },
            },
            ["created"] = "1970-01-01T00:00:00Z",
        };

        return JsonSerializer.SerializeToUtf8Bytes(config);
    }

    private static byte[] BuildManifestJson(string configDigest, long configSize, string layerDigest, long layerSize)
    {
        var manifest = new Dictionary<string, object>
        {
            ["schemaVersion"] = 2,
            ["mediaType"] = "application/vnd.docker.distribution.manifest.v2+json",
            ["config"] = new Dictionary<string, object>
            {
                ["mediaType"] = "application/vnd.docker.container.image.v1+json",
                ["size"] = configSize,
                ["digest"] = configDigest,
            },
            ["layers"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["mediaType"] = "application/vnd.docker.image.rootfs.diff.tar.gzip",
                    ["size"] = layerSize,
                    ["digest"] = layerDigest,
                },
            },
        };

        return JsonSerializer.SerializeToUtf8Bytes(manifest);
    }

    private static string ComputeDigest(byte[] data) => $"sha256:{Convert.ToHexStringLower(SHA256.HashData(data))}";

    internal sealed record ImageBuildResult(
        SeededImage Image,
        byte[] LayerGzipBytes,
        byte[] ConfigJsonBytes,
        byte[] ManifestJsonBytes);
}
