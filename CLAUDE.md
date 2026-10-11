# CLAUDE.md — AI Assistant Guide for Forgekeeper

Forgekeeper is a self-hosted 3D print file manager ("Plex for STL files"). It indexes, searches, previews and
organizes a large library (~200K files, 3.6 TB on ZFS over NFS) from MyMiniFactory, Thangs, Patreon, Cults3D and
manual sources. The MyMiniFactory (MMF) plugin also downloads the user's purchased library.

More detail: `docs/architecture.md`, `docs/mmf-plugin.md`, `docs/ops-runbook.md`, `docs/api-reference.md`,
`docs/configuration.md`. Wiki: `reference/forgekeeper` and `plans/forgekeeper-mmf-modernization`.

## Tech stack
- **Backend:** C# / ASP.NET Core 9 Minimal APIs, EF Core 9, PostgreSQL 16 (CNPG in prod)
- **Frontend:** Vue 3 (Composition API) + Vite + Tailwind v4, Three.js STL/OBJ viewer
- **Thumbnails:** stl-thumb (Rust CLI) → WebP. OBJ files are converted to a temp STL first; the renderer is killed on timeout (#84)
- **Plugins:** isolated `AssemblyLoadContext`, `ILibraryScraper` from `Forgekeeper.PluginSdk`
- **Sidecar:** `sidecars/mmf-fetch` (Python, curl_cffi) — Cloudflare-resistant fetcher used by the MMF plugin
- **Deploy:** GHCR image → Flux GitOps (`inxaos-flux`, `apps/forgekeeper`)

## Build and test
The dev host (ginaz) has **no .NET SDK**. Use the SDK container:

```bash
# from the repo root
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:9.0 dotnet build Forgekeeper.sln
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:9.0 dotnet test tests/Forgekeeper.Tests
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:9.0 \
  dotnet test tests/Forgekeeper.Tests --filter "FullyQualifiedName~MmfBoundedMemoryTests"

# or the long-lived dev container (hot reload, EF migrations)
docker compose -f docker-compose.dev.yml up -d
docker exec forgekeeper-dev bash -c "cd /src && dotnet build && dotnet test"

# EF migration
docker exec forgekeeper-dev bash -c "cd /src && dotnet ef migrations add <Name> \
  -p src/Forgekeeper.Infrastructure -s src/Forgekeeper.Api"

# frontend
cd src/Forgekeeper.Web && npm ci && npm run dev

# sidecar tests
cd sidecars/mmf-fetch && pip install -r requirements-test.txt && pytest
```

Builds take longer than 8s; run them in the background (`bgrun`) rather than blocking a session. CI
(`.github/workflows/build.yml`) builds, runs xUnit, pushes the image, then runs Playwright E2E against Docker
Compose. `mmf-fetch.yml` builds the sidecar image.

## Layout
```
src/
├── Forgekeeper.Api/             # Minimal API host
│   ├── Endpoints/               # one file per group (Models, Plugins, Scan, Maintenance, …)
│   ├── BackgroundServices/      # ScannerWorker, ThumbnailWorker, ImportWorker, PluginUpdateWorker
│   ├── Cli/                     # `forgekeeper plugin install|update|remove`
│   └── Program.cs               # DI, middleware, standalone endpoints (/metrics, /version, /mcp)
├── Forgekeeper.Core/            # entities, interfaces, DTOs, enums (no dependencies)
├── Forgekeeper.Infrastructure/  # ForgeDbContext + Migrations, services, repositories
├── Forgekeeper.PluginSdk/       # public SDK for plugin authors (SdkInfo.Version = "1.0.0")
└── Forgekeeper.Web/             # Vue SPA (all API calls via composables/useApi.js)
plugins/
├── Forgekeeper.Scraper.Mmf/     # built-in MMF plugin (v1.1.x) + manifest.json
└── manifest.schema.json         # plugin manifest schema
sidecars/mmf-fetch/              # Python fetch sidecar (own image: forgekeeper-mmf-fetch)
tests/Forgekeeper.Tests/         # xUnit unit + integration (SQLite/InMemory)
tests/Forgekeeper.E2E/           # Playwright
k8s/, charts/                    # generic example manifests (prod lives in inxaos-flux)
```

## Plugin system and manifest
- `PluginHostService` discovers plugin dirs, validates `manifest.json` (`ManifestValidationService`, schema in
  `plugins/manifest.schema.json`), checks SDK compatibility (`SdkCompatibilityChecker`) and loads each plugin
  **once**, together with its manifest (#79). Don't add code paths that load the assembly a second time.
- Two plugin locations: image-bundled `/app/plugins` (BuiltinPluginsDirectory) and installed `/data/plugins`.
  `Plugins:PreferBundled=true` makes the image win. **Prod must keep `Plugins__PreferBundled: "true"`**, or the
  stale v1.0.0 MMF plugin on `/data/plugins/mmf` loads instead.
- Plugin settings live in Postgres (`plugin_configs`), not env vars. Secrets are stored as `__token__*` keys.
- Sync progress: SSE at `/api/v1/plugins/{slug}/progress`; status at `/api/v1/plugins/{slug}/status` (`isRunning`).

## MMF sync safety (read before touching sync code or running one)
- **`DRY_RUN=true`** in `plugin_configs` unless the boss approved a real sync. Dry run writes reports to
  `/library/sources/mmf/.forgekeeper-reports/` and changes nothing.
- **`MAX_ITEMS`** caps downloads per run (skipped/already-present items don't use a slot). Use a small value for the
  first real run after any change; resume to continue.
- Start a fresh **Sync**, not **Resume**, unless you mean to continue a checkpoint. Stale `running` rows in
  `sync_runs` used to resume old checkpoints (#27, fixed).
- Partial-download folders and `.part` files are ignored and swept; a "success" with no files is not counted (#75 #77 #28).
- Memory is bounded (#74): downloads stream to disk, extraction is queued, and **`MAX_UNZIP_BACKLOG`** (default 2)
  makes downloads wait when that many archives are waiting to be extracted. Lower it if memory is tight.
- The plugin writes `dates.downloaded` in metadata.json and the scanner keeps `downloaded_at` (#76).
- **Watchdogs/log monitors:** match HTTP status codes in context (e.g. `HTTP 403`, `status=429`, `"StatusCode":503`),
  never bare numbers. A bare `403` matched byte counts and IDs and fired false alarms.
- **Never hard-delete** library files or rows. Move to a `.trash-*` dir (the scanner skips dot-dirs) and take a DB
  backup (`pg_dump` of the forgekeeper DB) first.
- **Data ops (merge, rehome, backfill, bulk moves) only when `isRunning=false`** for every plugin and no scan is
  running. The orphan-merge job enforces this and returns 409 otherwise.

## Maintenance API (`/api/v1/maintenance`)
All default to dry run; pass `apply=true` to write.
- `POST /mmf-orphans/merge?apply=&includeLive=&reportDir=` — **async** (#73): returns **202** with
  `statusUrl`; poll `GET /mmf-orphans/merge/status` (`status`, `blockedBy`). Returns **409** while a scan, a
  plugin sync or another maintenance job runs. Reports go to `/library/sources/mmf/.forgekeeper-reports`.
- `POST /source/backfill?apply=` — fix `source` on models (synchronous).
- MMF re-home: `POST /api/v1/plugins/mmf/rehome/plan`, then `.../rehome/apply?report=<file>.json` after review.

## Settings added recently
| Setting | Where | Default | Purpose |
|---|---|---|---|
| `MAX_UNZIP_BACKLOG` | plugin_configs (mmf) | 2 | download backpressure on the extraction queue (#74) |
| `MAX_ITEMS` | plugin_configs (mmf) | 0 (no cap) | cap per sync run |
| `DRY_RUN` | plugin_configs (mmf) | false in code — keep `true` in prod | report-only sync |
| `Scanner:RepairNonUtf8Names` | appsettings / env `Scanner__RepairNonUtf8Names` | false | rename non-UTF-8 file names to valid UTF-8 instead of only warning (#82) |
| `Plugins:PreferBundled` | env `Plugins__PreferBundled` | false — `true` in prod | image-bundled plugin wins |

## Deploy (prod)
1. Merge to `main`. CI pushes `:main`, `:<sha>`, immutable **`:sha-<short>`** and **`:main-<UTCts>-<sha>`** (#31).
2. Take the digest of the merge commit's build (run summary lists tags; or `docker buildx imagetools inspect
   ghcr.io/inxaos-repo/forgekeeper:sha-<short>`).
3. In **inxaos-flux** `apps/forgekeeper/release.yaml`, set `tag: main@sha256:<digest>` with a comment naming the
   commit. The **mmf-fetch sidecar** (`ghcr.io/inxaos-repo/forgekeeper-mmf-fetch`) is pinned the same way —
   bump it only when `sidecars/` changed. Branch `bob/*`, PR, `kubectl kustomize` check, merge, reconcile.
4. Keep the main container **memory limit at 4Gi** (request 512Mi). With #74 the MMF plugin stays well under it;
   if it OOMs, lower `MAX_UNZIP_BACKLOG` before raising the limit.
5. Done when: pod 1/1 with 0 restarts on the new digest, log shows `Loaded plugin: MyMiniFactory v1.1.x … /app/plugins`,
   migrations applied, no exceptions. Don't roll out while a sync is running unless the boss agrees — it kills it.

## Code rules
- EF: snake_case naming; all `[FromQuery]` params nullable; mark JSONB columns modified after edits; batch
  `SaveChangesAsync` at ≤50 entities; `.AsNoTracking()` for reads; avoid `.Any(...)` on JSONB collections (SQLite
  test provider can't translate — use `Count > 0` and filter in memory).
- Index migrations on big tables use `CREATE INDEX CONCURRENTLY` (non-transactional migration), see #81.
- API: bulk ops max 500; paging via `PaginatedResult<T>`; long work goes to a background job + status endpoint,
  never a request handler (the ingress cuts requests at 60s and RequestAborted cancels the work).
- Frontend: Anvil theme (`#1c1c1c` + amber `#f59e0b`), `forge-*` Tailwind classes, API via `useApi.js` only.

## Gotchas
1. Ingress rejects bodies >1 MB (413) and times out at 60s — upload manifests via `kubectl port-forward svc/forgekeeper 15000:5000`.
2. SPA fallback swallows API routes if static files are mapped before endpoints.
3. The scanner skips dot-directories (`.forgekeeper-reports`, `.trash-*`) — that's what makes trash safe.
4. Non-UTF-8 file names used to make the scanner skip files; now it warns, or repairs with `Scanner:RepairNonUtf8Names`.
5. `object-12345` in the MMF manifest = `12345` for the API.
6. Image is ~800MB (Chromium for the Playwright fallback); pulls take minutes. GHCR tags take ~30s to appear after push.
7. NFS is slow: never enumerate big directories inside a request handler.
8. `file_issues` lookups need the `model_id`/`variant_id` indexes (#56); don't drop them.
9. `k8s/` in this repo is an example, not prod. Prod is inxaos-flux.
10. Nothing syncs on a schedule unless `SYNC_INTERVAL_HOURS` > 0.
