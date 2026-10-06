namespace DockerMirror.Caching.S3;

internal record S3ListEntry(string Key, long Size, DateTimeOffset LastModified);
