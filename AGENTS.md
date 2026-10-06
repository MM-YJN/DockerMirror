# AGENTS.md

Guidance for AI coding agents working in this repository.

## Project overview

DockerMirror is a caching pull-through mirror/proxy for a Docker (OCI) container
registry, implementing the Docker Registry HTTP API v2. It sits in front of an
upstream registry (default `https://registry-1.docker.io`), proxies the `/v2/`
endpoints, caches blobs and manifests by digest, resolves tags to digests with
TTL revalidation, and supports FileSystem and S3 cache backends. It is built as a
Native-AOT, slim ASP.NET Core minimal-API app intended to ship as a chiseled
container image.

- **Language / runtime:** C# on .NET 10 (`net10.0`)
- **Framework:** ASP.NET Core minimal APIs (`WebApplication.CreateSlimBuilder`)
- **Testing:** xUnit v3 on Microsoft.Testing.Platform (MTP)
- **Versioning:** Nerdbank.GitVersioning
- **License:** MIT

Key endpoints (`source/DockerMirror/Registry/RegistryEndpoints.cs`,
`AdminEndpoints.cs`; health from `ServiceDefaults`):

- `GET|HEAD /v2/` — API version check (`Docker-Distribution-Api-Version: registry/2.0`)
- `GET|HEAD /v2/_catalog` — repository catalog (cached with TTL via `Mirror:Cache:Catalog` when enabled, default off; otherwise proxied live)
- `GET|HEAD /v2/{name}/tags/list` — image tags list (cached with TTL via `Mirror:Cache:TagsList` when enabled; cached responses preserve pagination `Link` headers)
- `GET|HEAD /v2/{name}/manifests/{ref}` — manifest by tag or digest (cached; tags revalidated via `Mirror:Cache:TagManifests:Ttl`; emits `ETag`, `Cache-Control` headers and honors `If-None-Match` with `304`; digest-addressed manifests are immutable and get `Cache-Control: immutable`; tag-addressed manifests use `max-age={Ttl}`; conditional upstream GET for tag revalidation via `Mirror:Cache:TagManifests:ConditionalRevalidation`; range requests for digest-addressed blobs/manifests served as `206` from cache hits on both FileSystem and S3 backends)
- `GET|HEAD /v2/{name}/blobs/{digest}` — blob by digest (cached, follows CDN redirects; transport-level `Content-Encoding` is auto-decompressed before hashing so the cached digest matches; emits `ETag`/`Cache-Control: immutable` and honors `If-None-Match` with `304`; digest-addressed 304 short-circuits before cache/upstream lookup since the digest *is* the content identity; range requests on cache hits support both FileSystem and S3 backends)
- `GET|HEAD /v2/{**path}` — any other `/v2/` resource (proxied live)
- `GET /admin/stats` — admin operational stats (disabled by default; see `Mirror:Admin`)
- `GET /health/live` — liveness probe (process self-check only; always at root)
- `GET /health/ready` — readiness probe (upstream + storage; always at root)

All `/v2/*` and `/admin/stats` paths are served relative to the optional
`Mirror:BasePath` (empty by default = root). When set to e.g. `/docker`, the
endpoints become `/docker/v2/*` and `/docker/admin/stats`. `/health/live` and
`/health/ready` always remain at root.

End-user docs (configuration reference, client setup, Docker image) live in
`README.md`. Keep both files in sync when behaviour or options change.

## Layout

```
source/DockerMirror/                # Main web app
  Caching/                          # Cache abstractions; FileSystem + S3 backends (S3/ has hand-rolled SigV4)
  Configuration/                    # Strongly-typed options classes (rooted at MirrorOptions)
  Json/                             # System.Text.Json source-gen context (AOT)
  Registry/                         # Registry v2 proxy/caching logic
  Program.cs                        # Entry point + DI composition
source/DockerMirror.AppHost/        # Aspire orchestrator (MinIO + mirror)
source/DockerMirror.ServiceDefaults/# Shared Aspire defaults (OpenTelemetry, health checks)
tests/DockerMirror.UnitTests/         # xUnit v3 unit tests
tests/DockerMirror.IntegrationTests/  # WebApplicationFactory + Testcontainers (MinIO)
tests/DockerMirror.E2ETests/          # DinD-based end-to-end real docker pull tests
tests/DockerMirror.TestKit/           # Shared test helpers
```

## Commands

Run from the repo root.

```bash
# First-time setup
dotnet tool restore        # restores local tools (nbgv, dotnet-inspect, etc.)
dotnet restore --locked-mode

# Build
dotnet build               # whole solution (DockerMirror.slnx)
dotnet build -c Release

# Run (listens on http://localhost:5041, Development env)
dotnet run --project source/DockerMirror

# Aspire orchestration (requires Docker; launches dashboard + MinIO + mirror)
aspire run
dotnet run --project source/DockerMirror.AppHost

# Test (Microsoft.Testing.Platform + xUnit v3)
dotnet test                                                            # all tests
dotnet test tests/DockerMirror.UnitTests/DockerMirror.UnitTests.csproj # one project
dotnet test tests/DockerMirror.E2ETests/DockerMirror.E2ETests.csproj   # E2E (needs Docker + privileged)
# Single/filtered test (MTP filter syntax):
dotnet run --project tests/DockerMirror.UnitTests -- --filter-query "/*/*/RegistryPathTests/*"

# Format / style
dotnet format                       # apply fixes per .editorconfig
dotnet format --verify-no-changes   # CI-style check

# AOT publish
dotnet publish source/DockerMirror/DockerMirror.csproj -c Release
```

Notes:
- `EnforceCodeStyleInBuild=true`: `.editorconfig` style violations surface as build warnings, and analyzers run during `dotnet build`. Keep the build warning-free.
- Integration/S3 tests need Docker (they spin up `pgsty/minio`). They self-skip via `Assert.Skip` when Docker is unavailable; do not treat those skips as failures.
- E2E tests need Docker with privileged containers (`docker:29-dind`). They **fail** (do not self-skip) when Docker is unavailable, since a valid test run requires real `docker pull` operations through the mirror.
- The main project exposes internals to the test projects via `InternalsVisibleTo`.
- **SDK pinning & package locks are enabled:** `global.json` pins the exact .NET
  SDK version, and `RestorePackagesWithLockFile=true` (in `Directory.Build.props`)
  makes NuGet maintain a committed `packages.lock.json` for every project.
  After adding, removing, or updating any .NET dependency, run
  `dotnet restore DockerMirror.slnx --force-evaluate` to refresh all lock files
  and commit the updated `packages.lock.json` files with the dependency change.

### Code coverage

Coverage is collected via the **coverlet** MTP extension (`coverlet.MTP`,
referenced by every test project). Local `dotnet test` runs do **not** collect
coverage by default — pass `--coverlet` to opt in. CI
(`.github/workflows/ci.yml`) collects Cobertura output and renders a Markdown
summary with `reportgenerator` into the GitHub Actions job summary.

```bash
# Collect coverage locally (Cobertura), then render an HTML report
dotnet test --results-directory ./test_results \
  --coverlet --coverlet-output-format cobertura \
  --coverlet-include "[DockerMirror]*,[DockerMirror.*]*" \
  --coverlet-exclude-by-file "**/*.g.cs,**/*.Generated.cs"
dotnet reportgenerator "-reports:./test_results/coverage.cobertura.*.xml" \
  "-targetdir:./test_results/coverage-report" "-assemblyfilters:+DockerMirror" \
  -reporttypes:Html
```

- Only product assemblies are measured (`[DockerMirror]*`, `[DockerMirror.*]*`);
  generated files (`*.g.cs`, `*.Generated.cs`) are excluded.
- Test projects and `DockerMirror.ServiceDefaults` opt out via the
  `[assembly: ExcludeFromCodeCoverage]` attribute declared in their `.csproj`.
- `reportgenerator` (local tool `dotnet-reportgenerator-globaltool`) converts
  the Cobertura XML into human-readable reports (`MarkdownSummary` in CI, any
  `-reporttypes` such as `Html` locally).

## Code style

Driven by `.editorconfig` (root, ~1600 lines) and `.gitattributes`. Match the existing code.

- **Line endings: CRLF for `.cs`/`.razor`** (enforced by `.gitattributes`), LF for `.sh`. UTF-8, final newline, no trailing whitespace.
- Indentation: 4 spaces for C#; 2 spaces for csproj/props/xml/json/yaml. Max line length 160.
- **Allman braces**; braces always required (even single-statement blocks). New line before `else`/`catch`/`finally`.
- `using` directives **outside** the namespace; `System.*` first; no separation between import groups.
- **File-scoped namespaces** matching folder structure. No file header.
- **Primary constructors preferred**; **`var` only when the type is apparent** (`.editorconfig` makes `var` for built-in types and elsewhere a warning); **target-typed `new()`** preferred.
- Prefer pattern matching, switch expressions, null propagation, `is null`, collection expressions, expression-bodied members (single line), UTF-8 string literals (`"..."u8`), range/index operators.
- No `this.` qualification; use language keywords (`int`, not `Int32`); accessibility modifiers required.

### Types & error handling (observed patterns)
- Types are typically `sealed` and `internal` by default; keep the public surface minimal.
- `partial` classes for source-generated logging (`[LoggerMessage]`).
- `readonly record struct` for small value types; `record` for DTOs.
- **`ConfigureAwait(false)` on every `await`** in app code (CA2007 is a warning).
- `CancellationToken` named `ct` in app code, passed last; use `TestContext.Current.CancellationToken` in tests.
- Validate args with `ArgumentNullException.ThrowIfNull(...)`.
- Use exception filters (`catch (Exception ex) when (...)`) over broad catches.

### AOT safety (important)
This app is `PublishAot=true`. Stay AOT-compatible:
- JSON goes through the source-gen context (`Json/RegistryJsonContext`); add new serialized types there. No reflection-based serialization.
- Logging uses source-gen `[LoggerMessage]`.
- Config binding uses the source generator; all options are bound under a single `MirrorOptions` root (sub-objects: `Upstream`, `Cache`, `Admin`). For composition-time reads before the DI container is built, read manually from `IConfiguration` (see `RegisterContentCache` in `Program.cs`).
- Avoid reflection, dynamic codegen, and unbounded generics that break trimming/AOT.

## Conventions for changes
- Keep `dotnet build` and `dotnet format --verify-no-changes` clean before finishing.
- Add or update tests in the appropriate project; test classes are `sealed`, use `[Fact]`/`[Theory]`/`[InlineData]`, AAA structure, and namespaces mirroring folders.
- Don't add new analyzer suppressions casually; existing suppressions in `Directory.Build.props` are documented with justifications.
- `README.md` is the end-user/contributor-facing guide; this file targets AI coding agents. Update both when behaviour or options change.

## Tooling
- Local dotnet tools (`dotnet-tools.json`): `nbgv`, `dotnet-outdated`, `reportgenerator`, `dotnet-inspect`, `aspire.cli`.
- For factual questions about .NET APIs / NuGet package contents, use the `dotnet-inspect` skill (`.agents/skills/dotnet-inspect/SKILL.md`) instead of guessing.

## Notes
- CI builds and tests on push/PR to `main` via `.github/workflows/ci.yml`. The
  `build` job runs `dotnet format --verify-no-changes` and the unit + integration
  tests; the separate `e2e` job runs the E2E suite on pushes to `main` and manual
  dispatches only (never on PRs), since it starts privileged Docker-in-Docker
  containers and pulls images from Docker Hub.
- CD publishes a multi-arch (`x64` + `arm64`) Native AOT container image to GHCR
  (`ghcr.io/mm-yjn/dockermirror`) via `.github/workflows/cd.yml` (manual `workflow_dispatch`).
  Per-arch images are tagged `<version>-x64` / `<version>-arm64`, with a
  multi-arch `<version>` manifest (NBGV `SimpleVersion`, e.g. `1.0.1`) in front;
  on `main` the same manifest is also pushed as `latest`.
- Versioning is handled by Nerdbank.GitVersioning (`version.json`).
