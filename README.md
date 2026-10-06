# DockerMirror

[![.NET](https://github.com/MM-YJN/DockerMirror/actions/workflows/ci.yml/badge.svg)](https://github.com/MM-YJN/DockerMirror/actions/workflows/ci.yml)

A caching **pull-through mirror / proxy** for a Docker (OCI) container registry,
built on ASP.NET Core with Native AOT (.NET 10). It implements the
[Docker Registry HTTP API v2](https://distribution.github.io/distribution/spec/api/),
sits in front of an upstream registry (default `https://registry-1.docker.io`),
proxies the `/v2/` endpoints, caches blobs and manifests by digest, resolves
tags to digests with TTL revalidation, and serves content from a pluggable
storage backend.

## Quick start

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### Build & run

```sh
dotnet tool restore
dotnet restore DockerMirror.slnx --locked-mode
dotnet build   DockerMirror.slnx
dotnet run --project source/DockerMirror/DockerMirror.csproj   # -> http://localhost:5041
```

### Docker

The release pipeline publishes a multi-arch Native AOT container image to GitHub
Container Registry:

```sh
docker pull ghcr.io/mm-yjn/dockermirror:latest
docker run -p 8081:8080 ghcr.io/mm-yjn/dockermirror:latest
```

`latest` tracks the `main` branch. Every CD run also publishes the version
reported by Nerdbank.GitVersioning (e.g. `1.0.1`) as a multi-arch manifest, with
arch-specific `1.0.1-x64` and `1.0.1-arm64` tags behind it:

```sh
docker pull ghcr.io/mm-yjn/dockermirror:1.0.1
```

The container listens on port **8080** and runs as a **non-root user** (UID
**1654**, from the chiseled base image).

#### Docker persistence & permissions

Docker named volumes (`-v name:/path`) mount as `root`, so writes to the volume
will fail unless you set up permissions.

When caching is enabled (`Mirror:Cache:Enabled=true`) with the `FileSystem`
backend, **the mirror checks at startup that the cache directory is writable**.
If it isn't, the container exits immediately with an actionable error message
instead of starting and later returning cryptic 500 errors.

**Working setup**

```sh
# Create and pre-own the volume (one-time setup)
docker volume create mirror-data
docker run --rm -v mirror-data:/data alpine chown 1654:1654 /data

# Run with caching enabled
docker run -d --name dockermirror \
  -e Mirror__Cache__Enabled=true \
  -e Mirror__Cache__FileSystem__Directory=/data \
  -v mirror-data:/data \
  -p 8081:8080 \
  ghcr.io/mm-yjn/dockermirror:latest
```

To serve under a subpath, add `-e Mirror__BasePath=/docker \` before the image name:

```sh
docker run -d --name dockermirror \
  -e Mirror__Cache__Enabled=true \
  -e Mirror__Cache__FileSystem__Directory=/data \
  -e Mirror__BasePath=/docker \
  -v mirror-data:/data \
  -p 8081:8080 \
  ghcr.io/mm-yjn/dockermirror:latest
```

Alternatively, run the container as your host user (`--user "$(id -u):$(id -g)"`).

A full `docker-compose.yml` example with eviction and an OpenTelemetry
collector is included in the `examples/` directory.

#### Building the container image locally

```sh
dotnet publish source/DockerMirror/DockerMirror.csproj --os linux --arch x64 /t:PublishContainer
```

Set `ContainerRegistry` and `ContainerImageTags` when pushing to a registry:

```sh
dotnet publish source/DockerMirror/DockerMirror.csproj --os linux --arch x64 /t:PublishContainer -p:ContainerRegistry=ghcr.io -p:ContainerImageTags='"latest;1.0"'
```

## Connecting clients

DockerMirror is a pull-through cache for Docker Hub. Point your container
runtime at it instead of registering it as a named source; image references
(`nginx`, `library/nginx:latest`, …) are unchanged. Single-segment names are
expanded with `Mirror:Upstream:DefaultNamespace` (default `library`), so
`nginx` resolves upstream as `library/nginx`.

### Docker Engine

Add the mirror to the daemon configuration (`/etc/docker/daemon.json`) and
restart Docker. The `registry-mirrors` mechanism applies to Docker Hub pulls
only:

```json
{
  "registry-mirrors": ["http://localhost:5041"]
}
```

```sh
sudo systemctl restart docker
docker pull nginx:latest   # served through the mirror
```

> [!NOTE]
> Docker requires HTTPS for registry mirrors unless the mirror host is listed
> under `insecure-registries`. For an HTTP mirror, add the host to
> `insecure-registries` in `daemon.json` or front DockerMirror with a TLS
> terminating reverse proxy.

### containerd / nerdctl / Kubernetes

containerd-based runtimes select a mirror via a `hosts.toml` host file. Create
`/etc/containerd/certs.d/docker.io/hosts.toml`:

```toml
server = "https://registry-1.docker.io"

[host."http://localhost:5041"]
  capabilities = ["pull", "resolve"]
```

## Configuration

All settings live under the `"Mirror"` key in `appsettings.json`. Every key can
also be supplied as an environment variable using the `__` separator
(e.g. `Mirror__Cache__Enabled=true`).

### Upstream (`Mirror:Upstream`)

| Key                | Type       | Default                              | Description                                            |
|--------------------|------------|--------------------------------------|--------------------------------------------------------|
| `RegistryUrl`      | `string`   | `https://registry-1.docker.io`       | Upstream registry base URL                             |
| `TokenRealm`       | `string`   | `https://auth.docker.io/token`       | Upstream token (auth) endpoint                         |
| `TokenService`     | `string`   | `registry.docker.io`                 | `service` parameter sent to the token endpoint         |
| `DefaultNamespace` | `string`   | `library`                            | Namespace prepended to single-segment repository names |
| `TokenCacheTtl`    | `TimeSpan` | `00:05:00`                           | Upper bound on how long bearer tokens are cached       |
| `Auth`             | `object?`  | `null`                               | Optional upstream credentials (see below)              |
| `Proxy`            | `object?`  | `null`                               | Optional outbound HTTP proxy (see below)               |
| `Resilience`       | `object`   | (see below)                          | Retry / circuit-breaker / timeout tuning               |

#### Upstream authentication (`Auth`)

Credentials are sent as HTTP Basic auth when fetching bearer tokens from the
token endpoint (useful for authenticated Docker Hub accounts or private
registries).

```json
"Auth": {
  "Username": "your-username",
  "Password": "your-password-or-token"
}
```

#### Outbound proxy (`Proxy`)

Routes upstream connections through an HTTP/HTTPS proxy. `Url` is the default
for all endpoints; the per-endpoint overrides take precedence when set.

| Key        | Type      | Default | Description                                         |
|------------|-----------|---------|-----------------------------------------------------|
| `Url`      | `string?` | `null`  | Default proxy for all upstream connections          |
| `Registry` | `string?` | `null`  | Proxy override for the registry endpoint            |
| `Auth`     | `string?` | `null`  | Proxy override for the token/auth endpoint          |
| `Cdn`      | `string?` | `null`  | Proxy override for blob redirect (CDN) downloads    |

DockerMirror automatically decompresses any transport-level `Content-Encoding`
(`gzip`/`deflate`/`br`) on upstream responses before hashing and caching, so the
cached blob digest always matches the OCI content digest. This keeps caching
correct even behind a proxy or CDN that applies transparent compression.

### Cache (`Mirror:Cache`)

| Key                | Type       | Default                | Description                                              |
|--------------------|------------|------------------------|----------------------------------------------------------|
| `Enabled`          | `bool`     | `false`                | Master switch for content caching                        |
| `Backend`          | `string`   | `FileSystem`           | `FileSystem` or `S3`                                     |
| `TagManifests`    | `object`   | `{"Enabled":true,"Ttl":"00:05:00","ConditionalRevalidation":true}` | Tag→digest resolver options                  |
| `TagsList`         | `object`   | `{"Enabled":true,"Ttl":"00:01:00","MaxEntries":1024,"MaxBodyBytes":1048576}` | Tags list in-memory cache              |
| `Catalog`          | `object`   | `{"Enabled":false,"Ttl":"00:01:00","MaxEntries":1024,"MaxBodyBytes":1048576}` | Catalog in-memory cache                 |
| `Headers`          | `object`   | `{"Enabled":true,"ImmutableMaxAge":"365.00:00:00"}` | HTTP protocol cache headers              |
| `WarmQueueCapacity`| `int`      | `256`                  | Bounded queue size for background cache-warm requests    |

Blobs and manifests are addressed by digest, which is immutable — so cached
content never goes stale. Mutable **tags** are resolved to a digest and the
pointer is revalidated once `TagManifests:Ttl` elapses. When `ConditionalRevalidation`
is enabled (default), stale pointers are revalidated with an upstream conditional
`GET` (using `If-None-Match`) instead of a separate `HEAD`, collapsing what would
be two round trips into one.

### HTTP protocol cache headers (`Mirror:Cache:Headers`)

When enabled, the mirror emits `ETag`, `Cache-Control`, and `Age` headers on
blob and manifest responses and honors client `If-None-Match` with `304 Not Modified`.

- **Digest-addressed** blobs and manifests are immutable: `Cache-Control: public, max-age=N, immutable`.
  A client `If-None-Match` matching the URL digest returns `304` immediately
  without consulting cache or upstream — the digest *is* the content identity.
- **Tag-addressed** manifests are mutable: `Cache-Control: public, max-age={TagManifests:Ttl}`.
- **`tags/list`** and **`_catalog`** use their respective TTLs.

`Age` is computed from the content's `StoredAtUtc` timestamp. `304` responses
include the same validators and are never cached by downstream intermediaries.

| Key               | Type        | Default              | Description                                      |
|-------------------|-------------|----------------------|--------------------------------------------------|
| `Enabled`         | `bool`      | `true`               | Master switch for ETag / `Cache-Control` / `Age` |
| `ImmutableMaxAge` | `TimeSpan`  | `365.00:00:00`       | `max-age` for digest-addressed immutable content |

### Tags list cache (`Mirror:Cache:TagsList`)

Short-TTL in-memory cache for `GET /v2/{name}/tags/list` responses. Cached entries
preserve the upstream `Link` header so paginated responses replay correctly.
Only `200` responses are cached (non-`200` is passed through). Active only when
the master switch (`Mirror:Cache:Enabled`) is `true`.

| Key            | Type       | Default      | Description                                        |
|----------------|------------|--------------|----------------------------------------------------|
| `Enabled`      | `bool`     | `true`       | Enable tags list caching                           |
| `Ttl`          | `TimeSpan` | `00:01:00`   | How long a cached tags list is considered fresh    |
| `MaxEntries`   | `int`      | `1024`       | Maximum number of cached list entries              |
| `MaxBodyBytes` | `long`     | `1048576`    | Response bodies larger than this are not cached    |

### Catalog cache (`Mirror:Cache:Catalog`)

Short-TTL in-memory cache for `GET /v2/_catalog` responses. Disabled by default
because Docker Hub does not support `_catalog`. Enabled for private registries
that expose the catalog endpoint. Same TTL and sizing options as `TagsList`.
Active only when the master switch (`Mirror:Cache:Enabled`) is `true`.

| Key            | Type       | Default      | Description                                        |
|----------------|------------|--------------|----------------------------------------------------|
| `Enabled`      | `bool`     | `false`      | Enable catalog caching (opt-in)                    |
| `Ttl`          | `TimeSpan` | `00:01:00`   | How long a cached catalog is considered fresh      |
| `MaxEntries`   | `int`      | `1024`       | Maximum number of cached list entries              |
| `MaxBodyBytes` | `long`     | `1048576`    | Response bodies larger than this are not cached    |

### Storage backends

**FileSystem** (default) — stores content on local disk under
`Mirror:Cache:FileSystem:Directory` (default `docker-mirror-cache`).

```json
"FileSystem": { "Directory": "docker-mirror-cache" }
```

**S3** — stores content in an S3-compatible bucket (MinIO, AWS, etc.) via a
hand-rolled, zero-dependency SigV4 client (AOT-safe, no AWS SDK).

```json
"S3": {
  "Bucket": "my-bucket",
  "Region": "us-east-1",
  "ServiceUrl": "http://localhost:9000",
  "AccessKey": "...",
  "SecretKey": "...",
  "KeyPrefix": "",
  "UsePathStyle": false
}
```

Set `UsePathStyle` to `true` for MinIO and other path-style endpoints.

### Cache eviction (`Mirror:Cache:Eviction`)

A background service periodically sweeps cached content and evicts entries
according to the configured policy. Evicted entries are re-downloaded from
upstream on the next request. Eviction only runs when the master switch
(`Mirror:Cache:Enabled`) is also `true`.

| Key                 | Type        | Default    | Description                                                  |
|---------------------|-------------|------------|--------------------------------------------------------------|
| `Enabled`           | `bool`      | `true`     | Enable periodic eviction sweeps (requires `Mirror:Cache:Enabled`) |
| `Strategy`          | `string`    | `Oldest`   | Size-eviction victim order: `Oldest` (creation time) or `Lru` (last access) |
| `MaxSizeBytes`      | `long?`     | `null`     | Maximum total cache size before size eviction kicks in       |
| `MaxAge`            | `TimeSpan?` | `null`     | Evict entries older than this (measured from fetch time)     |
| `Interval`          | `TimeSpan`  | `00:15:00` | Sweep period                                                 |
| `TargetUtilization` | `double`    | `0.9`      | Fraction of `MaxSizeBytes` to shrink to on a size sweep      |

`MaxAge` is strategy-independent (always measured from fetch time). `Strategy`
only selects the victim order for size-based eviction. On `FileSystem`, `Lru`
bumps `LastWriteTimeUtc` on cache hits; on `S3` it does a self-copy to refresh
`LastModified`.

### Cache size reporting (`Mirror:Cache:SizeReporting`)

Reports up-to-date cache size and entry count via metrics even when eviction is
disabled. When both eviction and reporting are enabled, eviction sweeps update
the gauges.

| Key        | Type        | Default      | Description                                  |
|------------|-------------|--------------|----------------------------------------------|
| `Enabled`  | `bool`      | `false`      | Enable periodic cache size enumeration       |
| `Interval` | `TimeSpan`  | `00:05:00`   | How often to enumerate for size reporting    |

### Negative cache (`Mirror:Cache:NegativeCache`)

Caches `404`/`410` responses in memory to avoid repeatedly hitting upstream for
non-existent blobs and manifests.

| Key          | Type       | Default    | Description                            |
|--------------|------------|------------|----------------------------------------|
| `Enabled`    | `bool`     | `true`     | Enable negative caching                |
| `Ttl`        | `TimeSpan` | `00:01:00` | How long a not-found result stays cached |
| `MaxEntries` | `int`      | `10000`    | Maximum number of cached not-found entries |

The negative cache is only consulted when `Mirror:Cache:Enabled=true`.

### Upstream resilience (`Mirror:Upstream:Resilience`)

Retry, circuit-breaker, and timeout policy for upstream HTTP calls (built on
`Microsoft.Extensions.Http.Resilience`).

| Key                   | Type       | Default    | Description                                           |
|-----------------------|------------|------------|-------------------------------------------------------|
| `Enabled`             | `bool`     | `true`     | Apply retries / circuit-breaking / Polly timeouts     |
| `MaxRetryAttempts`    | `int`      | `3`        | Retries on transient errors (range 0–10)              |
| `BaseDelay`           | `TimeSpan` | `00:00:01` | Base for exponential back-off with jitter             |
| `AttemptTimeout`      | `TimeSpan` | `00:00:10` | Per-attempt timeout for buffered (token) requests     |
| `TotalRequestTimeout` | `TimeSpan` | `00:00:30` | Total time across all attempts for buffered requests  |
| `HeadersTimeout`      | `TimeSpan` | `00:00:30` | Header-phase timeout for streaming (blob) requests    |
| `FailureRatio`        | `double`   | `0.1`      | Failure fraction that trips the circuit breaker       |
| `SamplingDuration`    | `TimeSpan` | `00:00:30` | Sliding window over which failures are counted        |
| `MinimumThroughput`   | `int`      | `10`       | Minimum requests in the window before the breaker trips |
| `BreakDuration`       | `TimeSpan` | `00:00:15` | How long the breaker stays open after tripping        |
| `ConnectTimeout`      | `TimeSpan` | `00:00:10` | TCP connect timeout (enforced even when `Enabled=false`) |

### Admin endpoint (`Mirror:Admin`)

Exposes a JSON endpoint with mirror version, uptime, upstream settings, cache
stats, and storage health. **Disabled by default.**

| Key       | Type     | Default          | Description                     |
|-----------|----------|------------------|---------------------------------|
| `Enabled` | `bool`   | `false`          | Enable the admin stats endpoint |
| `Path`    | `string` | `/admin/stats`   | URL path for the admin endpoint |

### Base path / subpath (`Mirror:BasePath`)

Places every `/v2/*` and `/admin/stats` endpoint under a configurable URL
prefix.  When `null` or unset (the default) all routes are served at the root.
`/health/live` and `/health/ready` always stay at root regardless of
`Mirror:BasePath`.

| Key        | Type      | Default | Description                              |
|------------|-----------|---------|------------------------------------------|
| `BasePath` | `string`  | `null`  | URL path prefix for `/v2` and admin endpoints |

Normalization — leading/trailing whitespace and trailing slashes are stripped.
The value is always prefixed with a single `/`.  These are equivalent:

```json
"BasePath": "docker"
"BasePath": "/docker"
"BasePath": "/docker/"
"BasePath": " /docker/ "
```

With `"Mirror:BasePath": "/docker"`:
- `/docker/v2/` — API version check
- `/docker/v2/_catalog` — catalog
- `/docker/v2/{name}/...` — manifests, blobs, etc.
- `/docker/admin/stats` — admin stats (when `Mirror:Admin:Enabled=true`)
- `/health/live`, `/health/ready` — still at root

The base path is a routing concern only; no upstream request paths are modified.

## Architecture

```
Client -> Mirror (/v2/, /v2/{name}/manifests|blobs/{ref})
            |
            +-- Tag manifest  -> resolve tag -> digest (TTL revalidation), serve cached body
            +-- Digest hit     -> serve from FileSystem / S3
            +-- Digest miss    -> fetch upstream, tee to client + cache (single-flight)
            +-- Not found      -> negative cache (404/410) to avoid re-probing upstream
```

- **Token auth**: a `DelegatingHandler` fetches and caches bearer tokens per
  scope from the upstream token service, transparently refreshing on `401`.
  Optional Basic credentials authenticate to the token endpoint.
- **Digest-addressed cache**: blobs and manifests requested by digest are
  immutable, so cache hits are served directly and misses are fetched once
  (concurrent requests for the same digest are coalesced with a per-key lock and
  tee'd into the cache while streaming to the client).
- **Tag manifest cache**: mutable tag references (e.g. `:latest`) are resolved
  to a digest pointer with a TTL; once stale, an upstream `HEAD` revalidates the
  pointer before serving the digest-addressed body.
- **Blob redirects**: upstream blob responses that redirect to a CDN are
  followed through a dedicated HTTP client with its own resilience pipeline.
- **Negative cache**: `404`/`410` results are remembered for a short TTL to
  avoid hammering upstream for missing content.
- **Native AOT**: the app is fully AOT-compiled. All JSON uses the
  source-generated `RegistryJsonContext`; there is no reflection-based
  serialization.

## Endpoints

| Route                       | Method(s)     | Description                                              |
|-----------------------------|---------------|----------------------------------------------------------|
| `/v2/`                      | `GET`, `HEAD` | API version check (`Docker-Distribution-Api-Version`)    |
| `/v2/_catalog`              | `GET`, `HEAD` | Repository catalog (cached with TTL via `Mirror:Cache:Catalog` when enabled, default off; otherwise proxied live) |
| `/v2/{name}/manifests/{ref}`| `GET`, `HEAD` | Manifest by tag or digest (cached; tags revalidated)     |
| `/v2/{name}/blobs/{digest}` | `GET`, `HEAD` | Blob by digest (cached, with CDN redirect follow; range requests served as `206 Partial Content` from cache hits on both FileSystem and S3 backends) |
| `/v2/{name}/tags/list`      | `GET`, `HEAD` | Tag listing (cached with TTL via `Mirror:Cache:TagsList` when enabled, default on; otherwise proxied live) |
| `/v2/{**path}`              | `GET`, `HEAD` | Any other `/v2/` resource (proxied live)                 |
| `/admin/stats`              | `GET`         | Admin stats (disabled by default; see `Mirror:Admin`)    |
| `/health/live`              | `GET`         | Liveness probe (always at root)                          |
| `/health/ready`             | `GET`         | Readiness probe (upstream reachability + storage availability; always at root) |

Only `GET`/`HEAD` requests to blobs and manifests addressed by digest are
cached; uploads and other write paths are not served by this mirror.

All `/v2/*` and `/admin/stats` paths are relative to the optional
`Mirror:BasePath`.  `/health/live` and `/health/ready` always remain at root.

## Observability

Metrics are emitted via the BCL `System.Diagnostics.Metrics` API under the meter
name `DockerMirror` (cache hit/miss, served bytes, upstream request duration,
eviction, negative cache, …) and can be scraped with `dotnet-counters` or
exported over OTLP by setting `OTEL_EXPORTER_OTLP_ENDPOINT`.

## Testing

Tests use **xUnit v3** on the **Microsoft.Testing.Platform** (MTP) runner.

| Project                         | Description                                | Docker     |
|---------------------------------|--------------------------------------------|------------|
| `DockerMirror.UnitTests`        | Unit tests (no external dependencies)      | Not needed |
| `DockerMirror.IntegrationTests` | `WebApplicationFactory` + S3 via MinIO     | Self-skips |
| `DockerMirror.E2ETests`         | DinD-based real `docker pull` through mirror | Required (fails without) |
| `DockerMirror.TestKit`          | Shared test helpers (not a test project)   | —          |

```sh
dotnet test                                                            # all tests
dotnet test tests/DockerMirror.UnitTests/DockerMirror.UnitTests.csproj # one project
```

Integration tests require Docker (they spin up `pgsty/minio`); they self-skip
when Docker is unavailable.

E2E tests require Docker with privileged containers (`docker:29-dind`) and
**fail** if Docker is absent — they perform real `docker pull` operations
through a fully booted mirror + registry.

### Code coverage

Coverage is collected by the [coverlet](https://github.com/coverlet-coverage/coverlet)
MTP extension (`coverlet.MTP`), referenced by every test project. Local
`dotnet test` runs do **not** collect coverage by default — pass `--coverlet`
to opt in. CI collects Cobertura output and publishes a Markdown summary
(rendered with `reportgenerator`) to the GitHub Actions job summary.

```sh
# Collect coverage (Cobertura) and render an HTML report locally
dotnet test --results-directory ./test_results \
  --coverlet --coverlet-output-format cobertura \
  --coverlet-include "[DockerMirror]*,[DockerMirror.*]*" \
  --coverlet-exclude-by-file "**/*.g.cs,**/*.Generated.cs"

dotnet reportgenerator "-reports:./test_results/coverage.cobertura.*.xml" \
  "-targetdir:./test_results/coverage-report" "-assemblyfilters:+DockerMirror" \
  -reporttypes:Html
```

Only the product assemblies (`[DockerMirror]*`, `[DockerMirror.*]*`) are measured;
generated files are excluded, and the test projects and
`DockerMirror.ServiceDefaults` opt out via `[assembly: ExcludeFromCodeCoverage]`.
`reportgenerator` is a local tool — run `dotnet tool restore` first.

## License

[MIT](LICENSE)
