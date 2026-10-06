namespace DockerMirror.Caching;

internal readonly record struct TagPointer(Digest Digest, DateTimeOffset ResolvedAtUtc);
