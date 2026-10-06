namespace DockerMirror.Configuration;

public sealed class AdminOptions
{
    public bool Enabled { get; set; }

    public string Path { get; set; } = "/admin/stats";
}
