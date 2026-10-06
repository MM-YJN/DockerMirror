namespace DockerMirror.Caching;

internal interface ITagPointerStore
{
    ValueTask<TagPointer?> TryGetTagAsync(string key, CancellationToken ct);

    ValueTask SetTagAsync(string key, TagPointer pointer, CancellationToken ct);
}
