namespace DockerMirror.Caching;

internal readonly record struct WarmRequest(string RewrittenName, string ResourceType, Digest Digest);
