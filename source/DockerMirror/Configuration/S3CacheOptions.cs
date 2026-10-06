namespace DockerMirror.Configuration;

public sealed class S3CacheOptions
{
    public string Bucket { get; set; } = "";

    public string Region { get; set; } = "us-east-1";

    public string? ServiceUrl { get; set; }

    public string AccessKey { get; set; } = "";

    public string SecretKey { get; set; } = "";

    public string KeyPrefix { get; set; } = "";

    public bool UsePathStyle { get; set; }
}
