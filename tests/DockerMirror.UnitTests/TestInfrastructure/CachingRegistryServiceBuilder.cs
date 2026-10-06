using DockerMirror.Caching;
using DockerMirror.Configuration;
using DockerMirror.Diagnostics;
using DockerMirror.Registry;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DockerMirror.UnitTests.TestInfrastructure;

// Fluent builder for CachingRegistryService in unit tests.
// Wires up all collaborators (CacheResponder, DigestResourceHandler,
// TagManifestHandler, NegativeCacheGate) from the same set of primitives
// used across the test suite, eliminating the 12-arg constructor repetition.
internal sealed class CachingRegistryServiceBuilder
{
    private readonly MirrorMetrics _metrics;
    private IOptions<MirrorOptions> _options = Options.Create(new MirrorOptions
    {
        Upstream = DefaultUpstreamOptions(),
        Cache = new CacheOptions { Enabled = true },
    });
    private IContentStore? _store;
    private ITagPointerStore? _tagStore;
    private KeyedAsyncLock? _keyedLock;
    private CacheWarmingQueue? _warmingQueue;
    private IHttpClientFactory _httpClientFactory = new ThrowingHttpClientFactory();
    private TimeProvider _timeProvider = TimeProvider.System;
    private HttpMessageHandler? _upstreamHandler;
    private ListResponseCache? _listCache;
    private UpstreamRegistryClient? _upstreamClient;

    internal CachingRegistryServiceBuilder(MirrorMetrics metrics) => _metrics = metrics;

    internal CachingRegistryServiceBuilder WithCacheOptions(CacheOptions options)
    {
        MirrorOptions current = _options.Value;
        _options = Options.Create(new MirrorOptions
        {
            Upstream = current.Upstream,
            Cache = options,
            Admin = current.Admin,
            BasePath = current.BasePath,
        });
        return this;
    }

    internal CachingRegistryServiceBuilder WithUpstreamOptions(UpstreamOptions options)
    {
        MirrorOptions current = _options.Value;
        _options = Options.Create(new MirrorOptions
        {
            Upstream = options,
            Cache = current.Cache,
            Admin = current.Admin,
            BasePath = current.BasePath,
        });
        return this;
    }

    // Sets the content store. When the store also implements ITagPointerStore the
    // same instance is used for both roles unless WithTagStore is called separately.
    internal CachingRegistryServiceBuilder WithStore(IContentStore store)
    {
        _store = store;

        if (_tagStore is null && store is ITagPointerStore ts)
        {
            _tagStore = ts;
        }

        return this;
    }

    internal CachingRegistryServiceBuilder WithTagStore(ITagPointerStore tagStore)
    {
        _tagStore = tagStore;
        return this;
    }

    internal CachingRegistryServiceBuilder WithKeyedLock(KeyedAsyncLock keyedLock)
    {
        _keyedLock = keyedLock;
        return this;
    }

    internal CachingRegistryServiceBuilder WithWarmingQueue(CacheWarmingQueue queue)
    {
        _warmingQueue = queue;
        return this;
    }

    internal CachingRegistryServiceBuilder WithHttpClientFactory(IHttpClientFactory factory)
    {
        _httpClientFactory = factory;
        return this;
    }

    internal CachingRegistryServiceBuilder WithTimeProvider(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        return this;
    }

    // Provide a raw HttpMessageHandler; the builder wraps it in an UpstreamRegistryClient.
    internal CachingRegistryServiceBuilder WithUpstreamHandler(HttpMessageHandler handler)
    {
        _upstreamHandler = handler;
        return this;
    }

    // Provide a fully-constructed UpstreamRegistryClient (overrides WithUpstreamHandler).
    internal CachingRegistryServiceBuilder WithUpstreamClient(UpstreamRegistryClient client)
    {
        _upstreamClient = client;
        return this;
    }

    internal CachingRegistryService Build()
    {
        UpstreamRegistryClient upstream = _upstreamClient ?? BuildUpstreamClient();
        IContentStore store = _store ?? new NullContentStore();
        ITagPointerStore tagStore = _tagStore ?? new NullTagPointerStore();
        KeyedAsyncLock keyedLock = _keyedLock ?? new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance, _metrics);
        CacheWarmingQueue warmingQueue = _warmingQueue ?? new CacheWarmingQueue(
            Options.Create(new MirrorOptions { Cache = new CacheOptions { WarmQueueCapacity = 256 } }), _metrics);

        var negativeCache = new NegativeCache(
            Options.Create(new MirrorOptions { Cache = new CacheOptions { NegativeCache = new NegativeCacheOptions { MaxEntries = 100 } } }),
            _metrics);

        var negGate = new NegativeCacheGate(
            negativeCache,
            _options,
            _metrics,
            NullLogger<NegativeCacheGate>.Instance);

        var cacheResponder = new CacheResponder(
            store,
            tagStore,
            _metrics,
            _timeProvider,
            _options,
            NullLogger<CacheResponder>.Instance);

        var digestHandler = new DigestResourceHandler(
            cacheResponder,
            negGate,
            upstream,
            keyedLock,
            warmingQueue,
            _httpClientFactory,
            _options,
            _timeProvider,
            _metrics,
            NullLogger<DigestResourceHandler>.Instance);

        var tagHandler = new TagManifestHandler(
            cacheResponder,
            negGate,
            upstream,
            keyedLock,
            _options,
            _timeProvider,
            _metrics,
            NullLogger<TagManifestHandler>.Instance);

        _listCache = new TagsListResponseCache(_options, _metrics, _timeProvider);
        var catalogCache = new CatalogListResponseCache(_options, _metrics, _timeProvider);
        var listHandler = new ListResourceHandler(
            upstream,
            keyedLock,
            _options,
            _timeProvider,
            (TagsListResponseCache)_listCache,
            catalogCache,
            _metrics,
            NullLogger<ListResourceHandler>.Instance);

        return new CachingRegistryService(
            _options,
            upstream,
            digestHandler,
            tagHandler,
            listHandler);
    }

    internal static HttpContext CreateHttpContext(string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = "/v2/" + path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private UpstreamRegistryClient BuildUpstreamClient()
    {
        HttpMessageHandler handler = _upstreamHandler ?? new NotFoundHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri(_options.Value.Upstream.RegistryUrl.TrimEnd('/') + "/") };
        return new UpstreamRegistryClient(http, _options, NullLogger<UpstreamRegistryClient>.Instance, _metrics);
    }

    private static UpstreamOptions DefaultUpstreamOptions()
        => new()
        {
            RegistryUrl = "https://registry.test.local",
            TokenRealm = "https://auth.test.local/token",
            TokenService = "registry.test.local",
        };

    // Stub IContentStore that always reports a cache miss.
    private sealed class NullContentStore : IContentStore
    {
        public ValueTask<bool> ExistsAsync(string key, CancellationToken ct)
            => ValueTask.FromResult(false);

        public ValueTask<CachedContent?> TryGetAsync(string key, CancellationToken ct)
            => ValueTask.FromResult<CachedContent?>(null);

        public ValueTask<ICacheWriteHandle> BeginWriteAsync(string key, Digest expectedDigest, CancellationToken ct)
            => throw new NotSupportedException("NullContentStore does not support writes.");
    }

    // Stub ITagPointerStore that always reports a miss and discards writes.
    private sealed class NullTagPointerStore : ITagPointerStore
    {
        public ValueTask<TagPointer?> TryGetTagAsync(string key, CancellationToken ct)
            => ValueTask.FromResult<TagPointer?>(null);

        public ValueTask SetTagAsync(string key, TagPointer pointer, CancellationToken ct)
            => ValueTask.CompletedTask;
    }

    // IHttpClientFactory that throws on any usage — for tests that never follow redirects.
    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => throw new NotSupportedException($"No HttpClient registered for '{name}' in this test.");
    }

    // HttpMessageHandler that returns 404 for every request.
    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }
}
