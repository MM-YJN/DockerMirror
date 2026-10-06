using DockerMirror.Configuration;

using Microsoft.Extensions.Options;

namespace DockerMirror.Registry;

public static class RegistryEndpoints
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        string basePath = app.Services.GetRequiredService<IOptions<MirrorOptions>>().Value.NormalizedBasePath;
        IEndpointRouteBuilder root = string.IsNullOrEmpty(basePath) ? app : app.MapGroup(basePath);
        RouteGroupBuilder v2 = root.MapGroup("/v2");

        v2.MapMethods("/", [HttpMethods.Get, HttpMethods.Head], static (HttpContext context) =>
        {
            context.Response.Headers[RegistryResponses.DockerDistributionApiVersion] = RegistryResponses.DockerDistributionApiVersionValue;
            return Results.Ok();
        });

        v2.MapMethods("/_catalog", [HttpMethods.Get, HttpMethods.Head],
            static async (UpstreamRegistryClient upstream, ListResourceHandler listHandler,
                IOptions<MirrorOptions> mirrorOptions, HttpContext context, CancellationToken ct) =>
            {
                if (mirrorOptions.Value.Cache.Enabled && mirrorOptions.Value.Cache.Catalog.Enabled)
                {
                    return await listHandler.HandleCatalogAsync(context.Request, context.Response, ct)
                        .ConfigureAwait(false);
                }

                await upstream.ForwardRawAsync("v2/_catalog", context.Request, context.Response, ct)
                    .ConfigureAwait(false);
                return Results.Empty;
            });

        v2.MapMethods("/{**path}", [HttpMethods.Get, HttpMethods.Head],
            static async (string? path, CachingRegistryService caching, HttpContext context, CancellationToken ct) =>
            {
                if (string.IsNullOrEmpty(path))
                {
                    return Results.NotFound();
                }

                return await caching.HandleAsync(path, context, ct).ConfigureAwait(false);
            });
    }
}
