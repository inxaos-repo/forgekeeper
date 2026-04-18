# CLAUDE.md — AI Assistant Guide for Forgekeeper

## What Is This Project?
Forgekeeper is a self-hosted 3D print file manager — "Plex for STL files." It indexes, organizes, searches, and previews massive collections (200K+ files, 3.6TB) from multiple sources (MyMiniFactory, Thangs, Patreon, Cults3D, manual).

## Tech Stack
- **Backend:** C# / ASP.NET Core 9 (Minimal APIs), Entity Framework Core 9, PostgreSQL 16
- **Frontend:** Vue.js 3 (Composition API) + Vite + Tailwind CSS v4
- **3D Preview:** Three.js with STLLoader
- **Thumbnails:** stl-thumb (Rust CLI) → WebP
- **Plugins:** Isolated AssemblyLoadContext, ILibraryScraper interface
- **Deployment:** Docker (multi-stage), Kubernetes via Flux GitOps, CNPG PostgreSQL

## Project Structure
```
src/
├── Forgekeeper.Api/              # ASP.NET Minimal API host + endpoints
│   ├── Endpoints/                # One file per resource group
│   ├── BackgroundServices/       # ScannerWorker, ThumbnailWorker, ImportWorker, PluginUpdateWorker
│   ├── Cli/                      # CLI mode (forgekeeper plugin install/update/remove)
│   └── Program.cs                # DI registration, middleware, standalone endpoints
├── Forgekeeper.Core/             # Domain models, interfaces, DTOs (ZERO dependencies)
│   ├── Models/                   # Entity classes (Model3D, Creator, Variant, Tag, etc.)
│   ├── Interfaces/               # Repository + service interfaces
│   ├── DTOs/                     # Request/response objects
│   └── Enums/                    # SourceType, VariantType, etc.
├── Forgekeeper.Infrastructure/   # EF Core, services, repositories
│   ├── Data/                     # ForgeDbContext, Migrations
│   ├── Services/                 # All business logic services
│   └── Repositories/             # Data access
├── Forgekeeper.PluginSdk/        # Public SDK for plugin authors (ships as NuGet)
└── Forgekeeper.Web/              # Vue.js 3 SPA
    └── src/
        ├── views/                # Page components (ModelsList, ModelDetail, etc.)
        ├── components/           # Reusable UI components
        └── composables/          # useApi.js (all API calls)

plugins/
└── Forgekeeper.Scraper.Mmf/      # Built-in MyMiniFactory scraper plugin

tests/
├── Forgekeeper.Tests/            # xUnit unit + integration tests
└── Forgekeeper.E2E/              # Playwright E2E tests
```

## Critical Rules

### EF Core / Database
- **Snake_case naming:** `UseSnakeCaseNamingConvention()` — all tables, columns, indexes are snake_case
- **All [FromQuery] params MUST be nullable:** `int?`, `bool?`, `string?` — non-nullable causes 400 binding errors
- **JSONB columns** (PrintHistory, Components, Extra, PrintSettings): mark as `.IsModified = true` after changes
- **Batch SaveChangesAsync:** never save more than 50 entities at once (Postgres parameter limit ~65K)
- **Use .AsNoTracking()** on read-only queries
- **Don't use .Any(p => p.Property == "value")** in LINQ-to-SQL on JSONB collections — InMemory provider can't translate it. Use `.Count > 0` for the DB query, check properties at the app layer.

### API Conventions
- **Max 500 per bulk operation** — enforce on all bulk endpoints
- **Paginated responses** use `PaginatedResult<T>` with `Items`, `TotalCount`, `Page`, `PageSize`
- **Prometheus metrics** at `/metrics` (text/plain format)
- **SSE streaming** at `/plugins/{slug}/progress` for sync progress
- **metadata.json writeback** — when user edits are saved, `MetadataWritebackService` syncs them to disk

### Plugin System
- Plugins implement `ILibraryScraper` from `Forgekeeper.PluginSdk`
- Loaded via `AssemblyLoadContext` isolation in `PluginHostService`
- Each plugin has a `manifest.json` (validated by `ManifestValidationService`)
- SDK version compatibility checked by `SdkCompatibilityChecker`
- `SdkInfo.Version = "1.0.0"` — the host SDK version constant

### Frontend (Vue.js)
- **Anvil theme:** warm charcoal (#1c1c1c) + amber (#f59e0b) accent
- **All API calls** go through `useApi.js` composable — never use fetch/axios directly in components
- **Forge-* Tailwind classes:** `bg-forge-card`, `border-forge-border`, `text-forge-accent`, etc.
- **Large components:** PluginsView (1100+ lines), ModelsList (800+ lines) — consider extracting sub-components
- **No markdown tables** in user-facing text (Telegram/Discord can't render them)

### Sync & Downloads (MMF Plugin)
- **No sync timeout** — syncs run until complete or cancelled via API
- **Download fallback chain:** Bearer token → Session cookies → Playwright → Archive URL → Constructed archive URL
- **File pagination:** fetch all pages from `/api/v2/objects/{id}/files` (not just first 100)
- **object- prefix:** manifest stores `object-12345`, API wants `12345` — strip the prefix
- **Restore mode:** `RESTORE_MODE=true` skips lastSynced check, still skips existing files on disk
- **Resume:** `POST /plugins/{slug}/sync?resume=true` picks up from `LastProcessedIndex`

### Testing
- **330+ tests** — xUnit (unit + integration) + Playwright E2E
- **TestDbContextFactory** uses SQLite InMemory — some Postgres features don't work (pg_trgm, JSONB .Any())
- **ForgeTestFactory** (WebApplicationFactory) for API integration tests
- **E2E tests** run against Docker Compose in GHA CI

### Common Pitfalls (Learned the Hard Way)
1. **InMemory EF provider** can't translate `PrintHistory.Any(p => p.Result == "success")` — use `Count > 0`
2. **Postgres parameter limit** (~65K) — batch SaveChangesAsync at 50 entities max
3. **SPA fallback** catches `/metrics` and other API routes if UseStaticFiles is before MapGet — order matters
4. **Docker image is ~800MB** (includes Chromium for Playwright) — image pulls take 2+ minutes
5. **GHCR manifest propagation** takes ~30s after push — E2E step needs retry loop
6. **NFS file operations** are slow — don't enumerate large directories synchronously in request handlers
7. **docker-compose.yml image ref** uses `$FORGEKEEPER_IMAGE` env var — CI sets it to GHCR tag, local uses build

## Building & Testing

### Local Development
```bash
# Backend (in dev container or with .NET 9 SDK)
docker compose -f docker-compose.dev.yml up -d
docker exec forgekeeper-dev bash -c "cd /src && dotnet build"
docker exec forgekeeper-dev bash -c "cd /src && dotnet test"

# Frontend
cd src/Forgekeeper.Web && npm run dev

# Full production build
docker compose build && docker compose up -d
```

### Running Tests
```bash
# All tests
dotnet test

# Specific test class
dotnet test --filter "ClassName=NewEndpointTests"

# E2E (requires Docker Compose)
docker compose --profile test run --rm e2e
```

## Key Services & What They Do
| Service | Purpose |
|---------|---------|
| `FileScannerService` | Walks source directories, reads metadata.json, upserts models |
| `SearchService` | pg_trgm fuzzy search with all filter params |
| `PluginHostService` | Plugin discovery, loading, sync orchestration |
| `MetadataWritebackService` | Syncs user edits back to metadata.json on disk |
| `ManifestValidationService` | Validates plugin manifest.json files |
| `SdkCompatibilityChecker` | Checks plugin SDK version compatibility |
| `FilenameTemplateParser` | Regex-based template parsing for "Guess from Filename" |
| `GitHubReleaseResolver` | Resolves plugin releases from GitHub |
| `PluginInstallService` | Download, verify, extract, install plugins |
| `PluginRegistryClient` | Fetches and caches the plugin registry |
| `PluginUpdateTracker` | Tracks available plugin updates for UI badges |
| `ImportService` | Processes unsorted files, auto-detect, confidence scoring |
| `ThumbnailService` | Generates STL → WebP thumbnails via stl-thumb |

## Database Entities
| Entity | Table | Notes |
|--------|-------|-------|
| `Model3D` | `models` | Core entity. JSONB: PrintHistory, Components, PrintSettings, Extra |
| `Creator` | `creators` | Name + source + model count |
| `Variant` | `variants` | File-level: path, size, type, hash |
| `Tag` | `tags` | Many-to-many with models via `model_tags` |
| `Source` | `sources` | Configured scan sources |
| `SyncRun` | `sync_runs` | Plugin sync history |
| `ScanState` | `scan_states` | Per-directory scan timestamps |
| `ImportQueueItem` | `import_queue` | Items pending import review |
| `PluginConfig` | `plugin_configs` | Per-plugin key-value config |
| `SavedTemplate` | `saved_templates` | Named templates for filename parsing/reorganization |
| `ModelRelation` | `model_relations` | Related models (many-to-many self-join) |

## API Overview (70+ endpoints)
See `docs/api-reference.md` for the full list. Key groups:
- `/api/v1/models` — CRUD, search, bulk ops, rename, reorganize, parse-filename, duplicates
- `/api/v1/creators` — list (paginated), detail, models
- `/api/v1/plugins` — list, sync, cancel, auth, config, history, diagnostics, install/update/remove, registry, reload
- `/api/v1/scan` — trigger, progress, untracked, verify, health
- `/api/v1/import` — process, queue, confirm, dismiss, watch-directories, scan-directory
- `/api/v1/files` — browse (server-side file browser)
- `/api/v1/templates` — CRUD for saved filename/reorganize templates
- `/api/v1/export` + `/import/restore` — backup/restore
- `/api/v1/settings` — read-only config view
- `/api/v1/stats` — collection statistics
- `/metrics` — Prometheus
- `/version` — build info
- `/mcp/*` — MCP interface (18 tools)
