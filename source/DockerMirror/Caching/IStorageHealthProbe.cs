namespace DockerMirror.Caching;

internal interface IStorageHealthProbe
{
    ValueTask CheckAsync(CancellationToken ct);
}
