using System.Text.Json.Serialization;

using DockerMirror.Diagnostics;

namespace DockerMirror.Json;

[JsonSerializable(typeof(MirrorStatsResponse))]
[JsonSerializable(typeof(TokenResponse))]
internal sealed partial class RegistryJsonContext : JsonSerializerContext
{
}

public sealed record TokenResponse
{
    [JsonPropertyName("token")]
    public string? Token { get; init; }

    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    [JsonPropertyName("expires_in")]
    public int? ExpiresIn { get; init; }

    [JsonPropertyName("issued_at")]
    public DateTime? IssuedAt { get; init; }
}
