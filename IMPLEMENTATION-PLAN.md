# Forgekeeper — Implementation Plan

> **Version:** 1.0 | **Created:** 2026-04-16 | **Audience:** Engineers building Forgekeeper  
> **Source Documents:** [Spec](https://wiki.k8s.example.com/plans/forgekeeper/spec) · [Architecture](https://wiki.k8s.example.com/plans/forgekeeper/architecture) · [Dev Environment](https://wiki.k8s.example.com/plans/forgekeeper/dev-environment) · [Library Scraper Plugin](https://wiki.k8s.example.com/plans/forgekeeper/library-scraper-plugin) 

---

## 1. Project Overview

### What Is Forgekeeper?

Forgekeeper is a **self-hosted 3D print file manager** — think "Plex for STL files." It indexes, organizes, searches, and previews a massive collection of 3D printing files (3.9 TB, 346K files, 55K directories) from multiple sources, presenting them in a single unified web interface.

### Who Is It For?

Tabletop wargaming hobbyists and 3D printing enthusiasts who download models from MyMiniFactory, Thangs, Patreon, Cults3D, Thingiverse, and other sources. The primary user has 165+ creators, 7,223 MMF models, and files spanning Warhammer 40K, Age of Sigmar, D&D, and general fantasy/sci-fi miniatures.

### What Problem Does It Solve?

Existing tools (Manyfold) choke at scale, treat supported/unsupported variants as separate models, lack creator intelligence, and provide weak search. Forgekeeper handles 500K+ files with sub-200ms search, proper variant grouping, rich metadata from `metadata.json` sidecar files, and an extensible plugin system for automated downloads.

### Technology Stack

| Layer | Technology | Notes |
|-------|-----------|-------|
| **Backend** | C# / ASP.NET Core 9, Minimal APIs | Owner's primary language |
| **ORM** | Entity Framework Core 9 + Npgsql | Migrations, LINQ, JSONB support |
| **Database** | PostgreSQL 16 + pg_trgm | CNPG operator on Longhorn in k8s |
| **Frontend** | Vue.js 3 (Composition API) + Tailwind CSS | SPA with Vue Router |
| **3D Viewer** | Three.js + STLLoader + OrbitControls | In-browser STL preview |
| **Thumbnails** | stl-thumb (Rust CLI) → WebP | Background generation |
| **Deployment** | Docker (multi-stage) → Kubernetes via Flux GitOps | Ingress at `forge.example.com` |
| **Storage** | NFS mount to `/mnt/3dprinting/` | Source of truth for files |
| **Testing** | xUnit, Moq, Testcontainers | Integration tests preferred |

### Repository Structure

```
Forgekeeper/
├── src/
│   ├── Forgekeeper.Api/              # ASP.NET Core Minimal API host
│   │   ├── Program.cs                # Service registration, middleware
│   │   ├── Endpoints/                # Endpoint groups (Model, Creator, Scan, etc.)
│   │   └── BackgroundServices/       # ScannerWorker, ThumbnailWorker
│   ├── Forgekeeper.Core/             # Domain models, interfaces, DTOs (zero dependencies)
│   │   ├── Models/                   # Creator, Model3D, Variant, Tag, Source, etc.
│   │   ├── Interfaces/               # IModelRepository, IScannerService, ILibraryScraper, etc.
│   │   ├── DTOs/                     # Request/response objects
│   │   └── Enums/                    # SourceType, VariantType, FileType, etc.
│   ├── Forgekeeper.Infrastructure/   # EF Core, services, repositories
│   │   ├── Data/                     # ForgeDbContext, Migrations, EntityConfigurations
│   │   ├── Services/                 # FileScannerService, SearchService, ImportService, ThumbnailService
│   │   ├── Repositories/             # ModelRepository, CreatorRepository
│   │   └── SourceAdapters/           # MmfSourceAdapter, PatreonSourceAdapter, GenericSourceAdapter
│   ├── Forgekeeper.PluginHost/       # Plugin loading, lifecycle, scheduling
│   ├── Forgekeeper.PluginSdk/        # ILibraryScraper + supporting types (NuGet package)
│   └── Forgekeeper.Web/              # Vue.js 3 SPA
│       ├── src/views/                # BrowseView, ModelDetailView, CreatorDetailView, etc.
│       ├── src/components/           # ModelCard, StlViewer, TagEditor, SearchBar, etc.
│       └── src/composables/          # useSearch, useModels, useImport, useViewer
├── tests/
│   ├── Forgekeeper.Tests/            # Unit + integration tests
│   └── Forgekeeper.PluginHost.Tests/
├── plugins/
│   └── Forgekeeper.Scraper.Mmf/      # Built-in MMF scraper plugin
├── docker-compose.yml                # Production compose
├── docker-compose.dev.yml            # Dev environment (SDK + PG + Node)
├── Dockerfile                        # Multi-stage production build
├── init-db.sql                       # pg_trgm extension creation
└── Forgekeeper.sln
```

### Dependency Rules (CRITICAL)

| Project | May Reference | External Packages |
|---------|--------------|-------------------|
| **Forgekeeper.Core** | _(nothing)_ | _(none — pure C#)_ |
| **Forgekeeper.PluginSdk** | Core | _(minimal — ships as NuGet)_ |
| **Forgekeeper.Infrastructure** | Core | EF Core 9, Npgsql, FluentValidation |
| **Forgekeeper.PluginHost** | Core, PluginSdk | Microsoft.Extensions.DependencyInjection |
| **Forgekeeper.Api** | Core, Infrastructure, PluginHost | ASP.NET Core 9, Serilog |
| **Forgekeeper.Web** | _(standalone SPA)_ | Vue 3, Three.js, Tailwind CSS, Vite |
| **Forgekeeper.Tests** | Core, Infrastructure | xUnit, Moq, Testcontainers |

Core has **zero** external dependencies. Infrastructure implements Core interfaces. Never let Infrastructure types leak into API endpoint signatures.

---

## 2. Sprint Plan / Work Breakdown

### WP1: Project Scaffolding + Database + Docker Dev Environment

**Objective:** Establish the project structure, Docker dev environment, database, and CI foundation so all subsequent work packages have a working build-test-run loop.

**Dependencies:** None (first work package)

**Deliverables:**
- Solution builds with `dotnet build` inside the dev container
- PostgreSQL 16 running with pg_trgm extension enabled
- EF Core migrations apply cleanly
- `docker compose -f docker-compose.dev.yml up -d` brings up the full dev stack
- API starts and returns 200 on a health check endpoint

**Estimated effort:** 3 days

**Detailed Tasks:**

| # | Task | Acceptance Criteria |
|---|------|-------------------|
| 1.1 | Create `Forgekeeper.sln` with all projects (Api, Core, Infrastructure, Web, Tests) | `dotnet build` succeeds with zero warnings |
| 1.2 | Configure `docker-compose.dev.yml` with three services: `dev` (.NET 9 SDK), `db` (Postgres 16), `frontend` (Node 22) | All containers start, `forgekeeper-db` reports healthy |
| 1.3 | Create `init-db.sql` with `CREATE EXTENSION IF NOT EXISTS pg_trgm;` | Extension available in the dev database |
| 1.4 | Configure `ForgeDbContext` with all entity mappings (see §3 Data Model) | `dotnet ef migrations add Initial` generates a valid migration |
| 1.5 | Add health check endpoint: `GET /health` → 200 OK | `curl http://localhost:5000/health` returns 200 |
| 1.6 | Configure `appsettings.Development.json` with connection string and storage paths | API connects to database on startup |
| 1.7 | Set `DOTNET_USE_POLLING_FILE_WATCHER=true` in dev compose | `dotnet watch` detects file changes over SMB |
| 1.8 | Add `.gitignore` for .NET, Node, IDE files | No build artifacts in git |

**Technical Notes:**
- The dev environment uses a split model: Visual Studio on Windows (your Windows desktop) edits via SMB share; Docker containers on Linux (your Linux server) build and run. See Dev Environment Guide for details.
- Use named volumes for `nuget-cache` and `node-modules` so packages persist across container recreations.
- Postgres runs on port 5433 (not 5432) to avoid conflicts with system Postgres.
- Environment variable `ASPNETCORE_URLS=http://+:5000` for the API.

**Existing Code:** The v0.1 codebase already has most of this scaffolding. Validate it matches the spec and fix any discrepancies.

---

### WP2: Domain Models + EF Core + Migrations

**Objective:** Implement all domain entities, EF Core configuration, and the initial database migration with proper indexes.

**Dependencies:** WP1

**Deliverables:**
- All entity classes in `Forgekeeper.Core/Models/`
- Complete `ForgeDbContext` with entity configurations
- Initial migration that creates all tables with proper indexes
- pg_trgm GIN indexes on `creators.name` and `models.name`
- JSONB columns for `PrintHistory`, `Components`, `Extra`, `PhysicalProperties`

**Estimated effort:** 3 days

**Detailed Tasks:**

| # | Task | Acceptance Criteria |
|---|------|-------------------|
| 2.1 | Implement `Creator` entity with all fields | Matches spec: id (UUID), name (unique per source), source, source_url, external_id, avatar_url, model_count, timestamps |
| 2.2 | Implement `Model3D` entity with all fields | Includes: JSONB PrintHistory, Components, Extra; computed Printed property; navigation to Creator, Source, Variants, Tags |
| 2.3 | Implement `Variant` entity with PhysicalProperties JSONB | file_hash field for dedup, PhysicalProperties JSONB |
| 2.4 | Implement `Tag` entity with many-to-many to Model3D | Junction table `model_tags` |
| 2.5 | Implement `Source` entity for configured sources | slug (unique), name, base_path, adapter_type, auto_scan |
| 2.6 | Implement `ScanState` entity | directory_path (unique), last_scanned_at, last_modified_at, file_count |
| 2.7 | Implement `ImportQueueItem` entity | All detection fields, confidence score, confirmed values, status |
| 2.8 | Implement `PrintRecord` entity (new — from architecture doc) | model_id FK, print_date, printer, technology, material, layer_height, scale_factor, result, notes |
| 2.9 | Implement all enums: SourceType, VariantType, FileType, ImportStatus, AcquisitionMethod | Stored as strings in DB via `.HasConversion<string>()` |
| 2.10 | Configure all EF entity mappings in `ForgeDbContext.OnModelCreating` | Indexes created: pg_trgm GIN on names, composite on (creator_id, name), partial on category/game_system |
| 2.11 | Generate and apply initial migration | `dotnet ef database update` succeeds; all tables created |
| 2.12 | Implement `SourceMetadata` C# model (metadata.json deserialization) | All fields from v1 schema including ID3-inspired fields: license, collection, sourceRating, relatedModels, printSettings, components, fileHashes, physicalProperties |

**Technical Notes:**
- Enums are stored as strings (`HasConversion<string>()`) for readability and forward compatibility.
- `Model3D.Printed` is a **computed property** (`PrintHistory?.Any(p => p.Result == "success")`), NOT stored in the database. Use `entity.Ignore(e => e.Printed)` in EF configuration.
- `Model3D.Extra` stores the raw `extra` field from metadata.json as a JSONB string.
- `Model3D.PreviewImages` is stored as `text[]` (PostgreSQL array).
- The `model_tags` junction table uses `UsingEntity("model_tags", ...)` configuration.
- `Variant.FilePath` has a unique index — no two variants should reference the same file.

**Key SQL Indexes (generated by EF migration):**

```sql
-- Fuzzy search indexes
CREATE INDEX idx_model_name_trgm ON models USING gin (name gin_trgm_ops);
CREATE INDEX idx_creator_name_trgm ON creators USING gin (name gin_trgm_ops);

-- JSONB index for metadata queries
CREATE INDEX idx_model_extra ON models USING gin (extra jsonb_path_ops);

-- Common browse patterns
CREATE INDEX idx_model_creator ON models (creator_id);
CREATE INDEX idx_model_source ON models (source);
CREATE INDEX idx_model_category ON models (category) WHERE category IS NOT NULL;
CREATE INDEX idx_model_game_system ON models (game_system) WHERE game_system IS NOT NULL;
CREATE INDEX idx_model_base_path ON models (base_path);  -- unique
CREATE INDEX idx_variant_model ON variants (model_id);
CREATE INDEX idx_variant_file_path ON variants (file_path);  -- unique
CREATE INDEX idx_scan_state_path ON scan_states (directory_path);  -- unique
CREATE INDEX idx_import_queue_status ON import_queue (status);
CREATE INDEX idx_source_slug ON sources (slug);  -- unique
CREATE INDEX idx_creator_name_source ON creators (name, source);  -- unique
```

---

### WP3: File Scanner Service + metadata.json Read/Write

**Objective:** Implement the core scanning engine that walks source directories, reads metadata.json files, detects variants, and upserts data into PostgreSQL.

**Dependencies:** WP2

**Deliverables:**
- `FileScannerService` that recursively walks source directories
- metadata.json parser using `SourceMetadata` model
- Fallback directory name parsing when metadata.json is absent
- Incremental scanning (only process new/changed directories)
- Variant detection from folder names and file patterns
- Source adapter pattern (`ISourceAdapter`) for source-specific parsing
- Scanner progress reporting via `ScanProgress` DTO

**Estimated effort:** 5 days

**Detailed Tasks:**

| # | Task | Acceptance Criteria |
|---|------|-------------------|
| 3.1 | Implement `ISourceAdapter` interface and `ParsedModelInfo` result | Interface defines: SourceType, SourceSlug, CanHandle(path), ParseModelDirectory(path) |
| 3.2 | Implement `MmfSourceAdapter` | Parses `sources/mmf/CreatorName/ModelName - ModelID/` structure; extracts creator name, model name, source ID from folder name |
| 3.3 | Implement `PatreonSourceAdapter` | Parses `sources/patreon/CreatorName/YYYY-MM Release Name/` with date-based releases; handles nested model subdirectories |
| 3.4 | Implement `GenericSourceAdapter` | Handles `sources/{slug}/CreatorName/ModelName/` for thangs, cults3d, thingiverse, manual sources |
| 3.5 | Implement variant detection logic | Maps folder names and file patterns to `VariantType` enum per the variant detection rules table (see §3) |
| 3.6 | Implement `FileScannerService.ScanAsync()` | Walks all configured source directories, delegates to source adapters, upserts Creator → Model3D → Variant records |
| 3.7 | Implement metadata.json reading | If `metadata.json` exists in model directory: deserialize to `SourceMetadata`, populate Model3D fields from it (fast path). If absent: fall back to folder name parsing (slow path). |
| 3.8 | Implement incremental scanning | Track last-modified timestamps per directory in `ScanState` table. On incremental scan, skip directories whose filesystem mtime hasn't changed since last scan. |
| 3.9 | Implement scan progress reporting | `ScanProgress` DTO with: IsRunning, Status, DirectoriesScanned, ModelsFound, FilesFound, NewModels, UpdatedModels, StartedAt, CompletedAt, ElapsedSeconds, Error |
| 3.10 | Implement file type detection | Map file extensions to `FileType` enum: .stl→Stl, .obj→Obj, .3mf→Threemf, .lys→Lys, .ctb→Ctb, .cbddlp→Cbddlp, .gcode→Gcode, .sl1→Sl1, .png→Png, .jpg→Jpg, .webp→Webp |
| 3.11 | Implement preview image detection | Find existing images in `images/` subfolder or `*.png/*.jpg` at model root. Store paths in `Model3D.PreviewImages` |
| 3.12 | Implement metadata.json writing for manual imports | When Forgekeeper creates a model from the unsorted/ flow, write a metadata.json to enable database-free recovery |
| 3.13 | Implement denormalized count updates | After scanning, update `Creator.ModelCount`, `Model3D.FileCount`, `Model3D.TotalSizeBytes` |

**Technical Notes:**
- **Performance target:** Full scan of 346K files in <30 minutes. Use parallel directory walking and batch DB inserts.
- **Pattern:** Use `Directory.EnumerateDirectories()` and `Directory.EnumerateFiles()` (lazy enumeration, not `GetFiles()` which loads all paths into memory).
- **Batch inserts:** Accumulate entities and call `SaveChangesAsync()` every 100-500 records, not per-record.
- **metadata.json field mapping:**

| metadata.json field | → Model3D property | Notes |
|--------------------|--------------------|-------|
| `name` | `Name` | |
| `source` | `Source` (enum parse) | |
| `externalId` | `SourceId` | |
| `externalUrl` | `SourceUrl` | |
| `description` | `Description` | |
| `tags[]` | `Tags` (M2M) | Union with user tags |
| `dates.created` | `ExternalCreatedAt` | |
| `dates.updated` | `ExternalUpdatedAt` | |
| `dates.downloaded` | `DownloadedAt` | |
| `license.type` | `LicenseType` | Denormalized for search |
| `collection.name` | `CollectionName` | Denormalized for grouping |
| `extra` | `Extra` (JSONB string) | Preserved verbatim |
| `printSettings` | (future) | Stored in metadata.json only for now |
| `components[]` | `Components` (JSONB) | |
| `creator.*` | `Creator` entity fields | Upsert by name+source |

- **Variant detection rules:**

| Folder/File Pattern | → `VariantType` |
|--------------------|----------------|
| `supported/`, `sup/` | `Supported` |
| `unsupported/`, `unsup/`, `nosup/` | `Unsupported` |
| `presupported/`, `pre-supported/`, `presup/` | `Presupported` |
| `lychee/`, `*.lys` | `LycheeProject` |
| `chitubox/`, `*.ctb`, `*.cbddlp` | `ChituboxProject` |
| `*.gcode` | `Gcode` |
| `*.3mf` | `PrintProject` |
| `images/`, `*.png`, `*.jpg` (non-model) | `PreviewImage` |
| Everything else (`.stl`, `.obj`) | `Unsupported` (default) |

- **Re-scanning is idempotent:** If a model directory already exists in the DB (matched by `BasePath`), update its fields. Don't create duplicates.
- Forgekeeper **never overwrites** a scraper-provided metadata.json. Only writes metadata.json for manually imported items.

---

### WP4: Search Service (pg_trgm)

**Objective:** Implement full-text fuzzy search across model names, creator names, and tags using PostgreSQL pg_trgm.

**Dependencies:** WP2, WP3

**Deliverables:**
- `SearchService` implementing `ISearchService`
- Multi-field search: query matches against model name, creator name, tags
- All filter parameters: creator, source, category, gameSystem, scale, fileType, printed, rating, licenseType, collectionName
- Sorting by: name, date, size, rating, file count
- Pagination with total count

**Estimated effort:** 3 days

**Detailed Tasks:**

| # | Task | Acceptance Criteria |
|---|------|-------------------|
| 4.1 | Implement `SearchService.SearchAsync()` with IQueryable composition | Returns `PaginatedResult<ModelResponse>` |
| 4.2 | Implement fuzzy text search using pg_trgm `similarity()` function | `?q=drago` matches "Dragon Knight" with similarity ≥ 0.3 |
| 4.3 | Implement all filter parameters | Each parameter narrows the result set when provided; null parameters are ignored |
| 4.4 | Implement sorting | `sort=name\|date\|size\|rating\|fileCount`, `order=asc\|desc` |
| 4.5 | Implement pagination | `page` (1-indexed), `pageSize` (default 50, max 200), return `totalItems`, `totalPages` |
| 4.6 | Implement printed status filter | `printed=true` → models with at least one successful print history entry; `printed=false` → models with no successful prints |
| 4.7 | Optimize search query performance | <200ms for any query against 10K+ models; use EXPLAIN ANALYZE to verify index usage |

**Technical Notes:**

- **pg_trgm query pattern** (use raw SQL via `FromSqlInterpolated` for the similarity function):

```csharp
// In SearchService
if (!string.IsNullOrWhiteSpace(request.Query))
{
    var q = request.Query;
    query = query.Where(m =>
        EF.Functions.TrigramsSimilarity(m.Name, q) > 0.3 ||
        EF.Functions.TrigramsSimilarity(m.Creator.Name, q) > 0.3 ||
        m.Tags.Any(t => EF.Functions.TrigramsSimilarity(t.Name, q) > 0.3));

    // Order by best similarity match when searching
    if (request.SortBy == null || request.SortBy == "relevance")
    {
        query = query.OrderByDescending(m =>
            EF.Functions.TrigramsSimilarity(m.Name, q));
    }
}
```

- **Alternative approach:** If Npgsql's EF Core pg_trgm integration is insufficient, use raw SQL:

```csharp
var models = await context.Models
    .FromSqlInterpolated($@"
        SELECT m.* FROM models m
        LEFT JOIN creators c ON m.creator_id = c.id
        WHERE similarity(m.name, {query}) > 0.3
           OR similarity(c.name, {query}) > 0.3
        ORDER BY greatest(similarity(m.name, {query}), similarity(c.name, {query})) DESC")
    .ToListAsync(ct);
```

- Set `pg_trgm.similarity_threshold` to `0.3` in PostgreSQL config.
- **Printed filter:** Since `Printed` is computed from `PrintHistory` JSONB, use a raw SQL condition: `WHERE print_history::jsonb @> '[{"result":"success"}]'` or filter in memory after loading if dataset is small enough.

---

### WP5: REST API Endpoints

**Objective:** Implement all REST API endpoints as ASP.NET Core Minimal APIs.

**Dependencies:** WP2, WP3, WP4

**Deliverables:**
- All CRUD endpoints for models, creators, variants, tags, sources
- Scan trigger and status endpoints
- Statistics endpoint
- Import queue endpoints
- File download (stream from NFS) and thumbnail serving
- Proper HTTP status codes and error responses

**Estimated effort:** 4 days

**Detailed Tasks:**

| # | Task | Acceptance Criteria |
|---|------|-------------------|
| 5.1 | Implement `ModelEndpoints` | GET /, GET /{id}, PATCH /{id}, DELETE /{id}, POST /{id}/prints, PUT /{id}/components |
| 5.2 | Implement `CreatorEndpoints` | GET / (list), GET /{id} (detail with stats), GET /{id}/models (paginated) |
| 5.3 | Implement `VariantEndpoints` | GET /{id}/download (stream file), GET /{id}/thumbnail (serve image) |
| 5.4 | Implement `TagEndpoints` | GET /tags (list all), POST /models/{id}/tags (add), DELETE /models/{id}/tags/{tag} (remove) |
| 5.5 | Implement `ScanEndpoints` | POST /scan (full), POST /scan/incremental, GET /scan/status |
| 5.6 | Implement `StatsEndpoints` | GET /stats (totals + breakdowns), GET /stats/creators (per-creator) |
| 5.7 | Implement `SourceEndpoints` | GET / (list), GET /{slug}, POST / (add), PATCH /{slug}, DELETE /{slug} |
| 5.8 | Implement `ImportEndpoints` | POST /import/process, GET /import/queue, POST /import/queue/{id}/confirm, DELETE /import/queue/{id} |
| 5.9 | Add request validation | Validate page > 0, pageSize 1-200, rating 1-5, required fields on create/update |
| 5.10 | Add global error handling middleware | Return consistent JSON error responses for 400, 404, 500 |

**API Contract (complete):**

```
Models:
  GET    /api/v1/models                        → PaginatedResult<ModelResponse>
  GET    /api/v1/models/{id}                   → ModelDetailResponse (includes variants, print history, components)
  PATCH  /api/v1/models/{id}                   → 200 OK (body: ModelUpdateRequest)
  DELETE /api/v1/models/{id}                   → 204 No Content (removes from DB only, NOT filesystem)
  POST   /api/v1/models/{id}/prints            → 201 Created (body: AddPrintRequest)
  PUT    /api/v1/models/{id}/components        → 200 OK (body: UpdateComponentsRequest)

Creators:
  GET    /api/v1/creators                      → List<CreatorResponse>
  GET    /api/v1/creators/{id}                 → CreatorDetailResponse (includes stats + models)
  GET    /api/v1/creators/{id}/models          → PaginatedResult<ModelResponse>

Variants:
  GET    /api/v1/variants/{id}/download        → File stream (Content-Type per file type)
  GET    /api/v1/variants/{id}/thumbnail       → Image/webp

Tags:
  GET    /api/v1/tags                          → List<Tag>
  POST   /api/v1/models/{modelId}/tags         → 200 OK (body: { "tags": ["tag1", "tag2"] })
  DELETE /api/v1/models/{modelId}/tags/{name}  → 204 No Content

Scan:
  POST   /api/v1/scan                          → ScanProgress (triggers full scan)
  POST   /api/v1/scan/incremental              → ScanProgress (triggers incremental)
  GET    /api/v1/scan/status                   → ScanProgress

Stats:
  GET    /api/v1/stats                         → StatsResponse
  GET    /api/v1/stats/creators                → List<CreatorStatsItem>

Sources:
  GET    /api/v1/sources                       → List<Source>
  GET    /api/v1/sources/{slug}                → Source detail with model count
  POST   /api/v1/sources                       → 201 Created (body: { slug, name, basePath, adapterType })
  PATCH  /api/v1/sources/{slug}                → 200 OK
  DELETE /api/v1/sources/{slug}                → 204 No Content

Import:
  POST   /api/v1/import/process                → List<ImportQueueItemDto> (triggers unsorted/ scan)
  GET    /api/v1/import/queue                  → List<ImportQueueItemDto> (optional ?status= filter)
  POST   /api/v1/import/queue/{id}/confirm     → 200 OK (body: ImportConfirmRequest)
  DELETE /api/v1/import/queue/{id}             → 204 No Content (dismiss)
```

**Search Parameters (GET /api/v1/models):**

| Parameter | Type | Description |
|-----------|------|-------------|
| `q` | string | Fuzzy search query (pg_trgm across name, creator, tags) |
| `creatorId` | Guid | Filter by creator |
| `source` | string | Filter by source enum (Mmf, Thangs, Patreon, etc.) |
| `sourceSlug` | string | Filter by source slug (mmf, thangs, etc.) — preferred over enum |
| `category` | string | Filter by category |
| `gameSystem` | string | Filter by game system |
| `scale` | string | Filter by scale (28mm, 32mm, 75mm) |
| `fileType` | string | Filter by file type enum |
| `printed` | bool | Filter by printed status |
| `minRating` | int | Minimum rating (1-5) |
| `licenseType` | string | Filter by license type |
| `collectionName` | string | Filter by collection name |
| `sortBy` | string | Sort field: name, date, size, rating, fileCount (default: name) |
| `sortDescending` | bool | Descending sort (default: false) |
| `page` | int | Page number, 1-indexed (default: 1) |
| `pageSize` | int | Results per page (default: 50, max: 200) |

**Response Envelope:**

```json
{
  "items": [...],
  "totalCount": 7223,
  "page": 1,
  "pageSize": 50,
  "totalPages": 145,
  "hasNext": true,
  "hasPrevious": false
}
```

**Technical Notes:**
- No authentication — single-user app behind ingress on private network.
- File download endpoint streams directly from NFS using `Results.File()` or `Results.Stream()`.
- DELETE on models removes the database record but **never** deletes files from the filesystem.
- Use `[AsParameters]` attribute on `ModelSearchRequest` for query string binding in Minimal APIs.

---

### WP6: Import Pipeline (unsorted/ Processing)

**Objective:** Implement the import pipeline that processes files dropped into the `unsorted/` directory, auto-sorts what it can, and queues the rest for user review.

**Dependencies:** WP3, WP5

**Deliverables:**
- `ImportService` that scans `unsorted/` for new files
- ZIP/RAR extraction
- Auto-detection of creator, model name, source from filenames and known creators
- Confidence scoring for auto-sort vs. user review
- File move from `unsorted/` to `sources/{source}/{creator}/{model}/`
- Import queue with user review/confirm flow
- metadata.json generation for imported items

**Estimated effort:** 4 days

**Detailed Tasks:**

| # | Task | Acceptance Criteria |
|---|------|-------------------|
| 6.1 | Implement `ImportService.ProcessUnsortedAsync()` | Scans unsorted/ directory, creates ImportQueueItem for each detected model/folder |
| 6.2 | Implement ZIP/RAR extraction | Automatically extract archives; detect model structure inside extracted contents |
| 6.3 | Implement filename parsing | Extract creator hints, model names, IDs from folder/file names using regex patterns |
| 6.4 | Implement creator matching | Query existing creators in DB to match detected creator names (fuzzy match) |
| 6.5 | Implement confidence scoring | Score 0.0-1.0 based on: known creator match (+0.4), source detected (+0.2), model name parsed (+0.2), variant pattern found (+0.2) |
| 6.6 | Implement auto-sort (high confidence ≥ 0.8) | Move files to `sources/{source}/{creator}/{model}/` automatically |
| 6.7 | Implement review queue (low confidence < 0.8) | Create `ImportQueueItem` with status `AwaitingReview` |
| 6.8 | Implement `ConfirmImportAsync()` | Accept user-confirmed creator/model/source, move files, create Model3D record |
| 6.9 | Implement `DismissAsync()` | Remove queue item, optionally delete source files |
| 6.10 | Write metadata.json for imported items | Create metadata.json in destination directory with known metadata for database-free recovery |
| 6.11 | Learn from user decisions | When user confirms a creator-to-source mapping, remember it for future imports |

**Technical Notes:**
- Use `System.IO.Compression.ZipFile` for ZIP extraction. For RAR, use SharpCompress NuGet package.
- After moving files, trigger a scan of the destination directory to create DB records.
- The import pipeline can run periodically (via `BackgroundService`) or on-demand via the API.

---

### WP7: Thumbnail Generation Service

**Objective:** Implement background thumbnail generation for STL/OBJ files using `stl-thumb`.

**Dependencies:** WP3

**Deliverables:**
- `ThumbnailService` that generates WebP thumbnails from STL files
- Background worker (`ThumbnailWorker`) that processes a queue of models needing thumbnails
- Thumbnails stored in `.forgekeeper/thumbnails/` on NFS
- Skip generation if thumbnail already exists

**Estimated effort:** 3 days

**Detailed Tasks:**

| # | Task | Acceptance Criteria |
|---|------|-------------------|
| 7.1 | Install `stl-thumb` in Docker image | `stl-thumb --version` works inside the container |
| 7.2 | Implement `ThumbnailService.GenerateThumbnailAsync()` | Calls `stl-thumb` via `Process.Start()`, generates 256×256 WebP |
| 7.3 | Implement `ThumbnailWorker` as `BackgroundService` | Queries models with `ThumbnailPath == null`, processes queue |
| 7.4 | Store thumbnails in `.forgekeeper/thumbnails/{model-id}.webp` | File exists on disk, path stored in `Model3D.ThumbnailPath` |
| 7.5 | Skip existing thumbnails | If file exists at expected path, don't regenerate |
| 7.6 | Handle errors gracefully | Log failures, continue processing queue; don't crash on corrupt STL files |
| 7.7 | Serve thumbnails via API endpoint | `GET /api/v1/variants/{id}/thumbnail` returns the WebP file |

**Technical Notes:**
- `stl-thumb` command: `stl-thumb input.stl output.webp --size 256`
- Thumbnail generation is CPU-intensive. Process one at a time with a configurable delay between renders.
- For the initial 346K file collection, thumbnail generation will take hours. Run as a background task.
- Use a separate thumbnail path per variant if per-variant thumbnails are needed, or per model (first STL found).
- Configuration in `appsettings.json`:

```json
"Thumbnails": {
  "Enabled": true,
  "Size": "256x256",
  "Format": "webp",
  "Renderer": "stl-thumb",
  "OutputDir": ".forgekeeper/thumbnails"
}
```

---

### WP8: Plugin Host + ILibraryScraper Interface

**Objective:** Implement the plugin host infrastructure that discovers, loads, configures, and manages scraper plugins.

**Dependencies:** WP2, WP5

**Deliverables:**
- `Forgekeeper.PluginSdk` project with `ILibraryScraper` interface and supporting types
- `Forgekeeper.PluginHost` project with assembly loading, lifecycle management, and scheduling
- Plugin discovery from `plugins/` directory
- Plugin configuration storage in PostgreSQL
- Admin API endpoints for plugin management
- OAuth callback routing to plugins

**Estimated effort:** 5 days

**Detailed Tasks:**

| # | Task | Acceptance Criteria |
|---|------|-------------------|
| 8.1 | Create `Forgekeeper.PluginSdk` project with ILibraryScraper interface | Interface matches spec: SourceSlug, SourceName, Description, Version, ConfigSchema, RequiresBrowserAuth, AuthenticateAsync, FetchManifestAsync, ScrapeModelAsync, HandleAuthCallbackAsync |
| 8.2 | Implement supporting types in SDK | `PluginContext`, `ScrapedModel`, `ScrapeResult`, `AuthResult`, `PluginConfigField`, `ScrapeProgress`, `DownloadedFile`, `MetadataFile` |
| 8.3 | Implement `PluginLoader` using `AssemblyLoadContext` | Load DLLs from `plugins/` directory, find ILibraryScraper implementations, instantiate |
| 8.4 | Implement `PluginHost` service | Manages lifecycle: Initialize → Authenticate → Ready. Provides PluginContext to each plugin. |
| 8.5 | Implement plugin configuration storage | `plugin_configs` table in PostgreSQL. Encrypted at rest for secret fields. |
| 8.6 | Implement plugin scheduler | `SyncSchedulerService` (BackgroundService) runs each plugin's sync on configurable interval |
| 8.7 | Implement sync execution flow | For each new/updated item from FetchManifestAsync: call ScrapeModelAsync, write metadata.json, trigger scanner |
| 8.8 | Implement OAuth callback routing | Route `/auth/{sourceSlug}/callback` to the appropriate plugin's HandleAuthCallbackAsync |
| 8.9 | Implement plugin admin API endpoints | GET /api/v1/plugins, GET /api/v1/plugins/{slug}/status, POST /api/v1/plugins/{slug}/sync, PUT /api/v1/plugins/{slug}/config |
| 8.10 | Implement plugin manifest.json reading | Read plugin metadata from sidecar JSON or embedded attributes |

**ILibraryScraper Interface (complete C# definition):**

```csharp
public interface ILibraryScraper
{
    string SourceSlug { get; }
    string SourceName { get; }
    string Description { get; }
    string Version { get; }
    IReadOnlyList<PluginConfigField> ConfigSchema { get; }
    bool RequiresBrowserAuth { get; }

    Task<AuthResult> AuthenticateAsync(PluginContext context, CancellationToken ct);
    Task<IReadOnlyList<ScrapedModel>> FetchManifestAsync(PluginContext context, CancellationToken ct);
    Task<ScrapeResult> ScrapeModelAsync(ScrapedModel model, PluginContext context, CancellationToken ct);
    Task<bool> HandleAuthCallbackAsync(HttpContext httpContext, PluginContext context);
    string? GetAdminPageHtml(PluginContext context) => null;
}
```

**Plugin Loading:**

```
plugins/
├── forgekeeper-scraper-mmf/
│   ├── Forgekeeper.Scraper.Mmf.dll
│   ├── manifest.json
│   └── deps/
```

**manifest.json:**

```json
{
  "pluginId": "forgekeeper-scraper-mmf",
  "version": "1.0.0",
  "author": "Forgekeeper Contributors",
  "repository": "https://github.com/forgekeeper/scraper-mmf",
  "entryAssembly": "Forgekeeper.Scraper.Mmf.dll",
  "entryType": "Forgekeeper.Scraper.Mmf.MmfScraperPlugin",
  "minForgekeeper": "1.0.0"
}
```

**Technical Notes:**
- Each plugin loads in its own `AssemblyLoadContext` for isolation. Unloading an old context before loading an updated DLL enables hot-update.
- Plugin config is stored in a `plugin_configs` table: `plugin_slug TEXT, key TEXT, value TEXT, is_secret BOOL`. Secret values encrypted with a key derived from a master secret in appsettings.
- The sync scheduler should respect per-plugin schedule configuration (e.g., MMF every 24h, Patreon weekly).
- When `ScrapeModelAsync` returns, Forgekeeper writes the metadata.json (Forgekeeper is the single owner) and triggers the scanner for the new directory.
- Plugins provide `IProgress<ScrapeProgress>` for real-time UI updates.

---

### WP9: MMF Scraper Plugin (Port from Legacy Downloader)

**Objective:** Port the legacy MMF downloader's core logic into a Forgekeeper plugin implementing `ILibraryScraper`.

**Dependencies:** WP8

**Deliverables:**
- `Forgekeeper.Scraper.Mmf` plugin DLL
- OAuth implicit flow authentication with MMF
- Library manifest fetching (browser console paste OR Playwright)
- File download with rate limiting and dedup
- Proper metadata.json output per Forgekeeper's v1 schema

**Estimated effort:** 5 days

**Detailed Tasks:**

| # | Task | Acceptance Criteria |
|---|------|-------------------|
| 9.1 | Create `Forgekeeper.Scraper.Mmf` project implementing ILibraryScraper | Compiles against PluginSdk, loads via PluginHost |
| 9.2 | Port OAuth AuthService | OAuth implicit flow to `auth.myminifactory.com`, callback handled by Forgekeeper's router |
| 9.3 | Port `ManifestService` | Accept uploaded manifest JSON (browser console paste); parse `objectPreviews` array into `ScrapedModel` list |
| 9.4 | Port `DownloadService` | Download files from MMF v2 API: `GET /api/v2/objects/{id}`, handle individual files and archive ZIPs |
| 9.5 | Implement `ScrapeModelAsync()` | Download all files to `context.ModelDirectory`, return `ScrapeResult` with file list and metadata |
| 9.6 | Map MMF manifest fields to metadata.json | `originalId`→externalId, `creatorUsername`→creator.username, `source`→acquisition.method, etc. |
| 9.7 | Implement rate limiting | Configurable delay between downloads (default 3000ms via CONFIG_SCHEMA) |
| 9.8 | Implement dedup | Skip models whose directory already exists with files |
| 9.9 | Implement update checking | `HasUpdateAsync()` compares `updatedAt` from manifest vs. last download date |
| 9.10 | Implement ConfigSchema | CLIENT_ID (string, required), CLIENT_SECRET (secret, required), DELAY_MS (number, default 3000) |

**MMF API Details (from MMF API analysis):**

| Parameter | Value |
|-----------|-------|
| Auth URL | `https://auth.myminifactory.com/web/authorize` |
| Client ID | `downloader_v2` |
| Client Secret | `6b511607-740d-49ad-8e31-3bb8b75dd354` |
| Redirect URI | Forgekeeper routes to `/auth/mmf/callback` |
| Response Type | `token` (implicit flow) |
| API base | `https://www.myminifactory.com/api/v2/` |
| Rate limit delay | 1.5s minimum between downloads (3s recommended) |

**Technical Notes:**
- CDN download URLs need a separate HttpClient without the Bearer token.
- Validate response isn't HTML (Cloudflare error page detection).
- Handle 404 (model delisted) and 403 (access denied) gracefully.
- Pipe characters (`|`) in names need reformatting: `"Name|Creator|ID"` → `"Name (Creator - ID)"`.
- ZIP extraction for single-archive downloads is handled by Forgekeeper (plugin returns `IsArchive=true`).

---

### WP10: Vue.js Frontend — Models List + Search + Filters

**Objective:** Implement the main browse/search page with model card grid and filter sidebar.

**Dependencies:** WP5

**Deliverables:**
- `BrowseView.vue` — main page with grid layout
- `ModelCard.vue` — thumbnail + title + source badge + metadata
- `SearchBar.vue` — search input with debounced API calls
- `FilterSidebar.vue` — faceted filters for all search parameters
- Responsive layout (desktop grid → mobile single column)
- Infinite scroll or pagination controls

**Estimated effort:** 5 days

**Detailed Tasks:**

| # | Task | Acceptance Criteria |
|---|------|-------------------|
| 10.1 | Set up Vue 3 project with Vite, Vue Router, Tailwind CSS | `npm run dev` serves at port 5173 |
| 10.2 | Configure Vue Router with routes | `/` → BrowseView, `/models/:id` → ModelDetailView, `/creators/:id` → CreatorDetailView, `/import` → ImportQueueView, `/stats` → StatsView |
| 10.3 | Implement `useSearch` composable | Manages search state, calls `GET /api/v1/models`, handles pagination |
| 10.4 | Implement `useApi` composable | Base HTTP client pointing to API server, handles errors |
| 10.5 | Implement `NavBar.vue` | Navigation links: Browse, Creators, Import, Stats; global search input |
| 10.6 | Implement `SearchBar.vue` | Text input with 300ms debounce, triggers search on type |
| 10.7 | Implement `FilterSidebar.vue` | Dropdowns/checkboxes: source, category, gameSystem, scale, printed, rating |
| 10.8 | Implement `ModelCard.vue` | Thumbnail (or placeholder), model name, creator name, source badge, file count, size, printed indicator |
| 10.9 | Implement `SourceBadge.vue` | Colored badge showing source (MMF=blue, Patreon=orange, etc.) |
| 10.10 | Implement `BrowseView.vue` | Grid layout: 4-6 cards/row desktop, 2-3 tablet, 1 mobile. SearchBar + FilterSidebar + ModelCard grid. |
| 10.11 | Implement pagination controls | Page numbers, next/prev, total count display |
| 10.12 | Add loading states and empty states | Skeleton loaders during fetch, "No models found" for empty results |

**Technical Notes:**
- API base URL configurable via Vite environment variable: `VITE_API_URL`.
- Thumbnail URL: `${API_URL}/api/v1/models/${model.id}/thumbnail` or use `model.thumbnailPath` if serving static files.
- Debounce search to avoid excessive API calls.
- Responsive breakpoints: sm (640px), md (768px), lg (1024px), xl (1280px).

---

### WP11: Vue.js Frontend — Model Detail + 3D Preview

**Objective:** Implement the model detail page with variant list, metadata editor, and Three.js STL viewer.

**Dependencies:** WP10

**Deliverables:**
- `ModelDetailView.vue` — full model page
- `StlViewer.vue` — Three.js 3D preview component
- `VariantList.vue` — grouped variant display with download links
- `TagEditor.vue` — inline tag add/remove
- `StarRating.vue` — clickable star rating
- Print history display and add form

**Estimated effort:** 5 days

**Detailed Tasks:**

| # | Task | Acceptance Criteria |
|---|------|-------------------|
| 11.1 | Implement `ModelDetailView.vue` | Fetches model detail via `GET /api/v1/models/{id}`, displays all metadata |
| 11.2 | Implement `StlViewer.vue` with Three.js | Loads STL from variant download URL, renders with OrbitControls (rotate/zoom/pan), ambient + directional lighting |
| 11.3 | Implement `VariantList.vue` | Groups variants by VariantType, shows file name, size, type badge, download button |
| 11.4 | Implement `TagEditor.vue` | Display tags as chips, click X to remove, input to add new tag. Calls POST/DELETE tag endpoints. |
| 11.5 | Implement `StarRating.vue` | 5 clickable stars, calls PATCH model endpoint to update rating |
| 11.6 | Implement metadata editor | Inline edit for category, scale, gameSystem, notes. PATCH model on save. |
| 11.7 | Implement print history display | List of print attempts with date, printer, material, result, notes |
| 11.8 | Implement `PrintHistoryForm.vue` | Form to add a print: date, printer, technology, material, layer height, scale, result, notes, variant selector |
| 11.9 | Implement component assembly view | If model has components: show "Build Your Model" with required parts checked, option groups as radio buttons |
| 11.10 | Implement variant selector for 3D viewer | Dropdown or tabs to switch between variants in the STL viewer |

**Three.js Viewer Requirements:**
- Load STL via `STLLoader` from `/api/v1/variants/{id}/download`
- Scene: dark background (`#1a1a2e`), AmbientLight + DirectionalLight
- Material: MeshPhongMaterial with color `#4ecca3` (teal)
- Auto-fit camera to model bounding box on load
- OrbitControls for user interaction
- Handle large STLs gracefully (loading indicator, error on failure)

---

### WP12: Vue.js Frontend — Creators, Import Queue, Stats

**Objective:** Implement the remaining frontend views.

**Dependencies:** WP10, WP11

**Deliverables:**
- `CreatorDetailView.vue` — creator page with model list and stats
- `CreatorsList.vue` — creator directory
- `ImportQueueView.vue` — review and confirm unsorted imports
- `StatsView.vue` — collection statistics dashboard

**Estimated effort:** 4 days

**Detailed Tasks:**

| # | Task | Acceptance Criteria |
|---|------|-------------------|
| 12.1 | Implement `CreatorsList.vue` | Paginated list of creators with model count, source badge, avatar |
| 12.2 | Implement `CreatorDetailView.vue` | Creator info + paginated model grid for that creator + stats (total size, file count, models) |
| 12.3 | Implement `ImportQueueView.vue` | List of pending imports with detected values, confidence bar, confirm/dismiss buttons |
| 12.4 | Implement import confirm form | Editable fields: creator, model name, source (dropdown). Pre-filled with detected values. |
| 12.5 | Implement `StatsView.vue` | Dashboard cards: total models, creators, files, size. Charts: by source, by category, printed ratio. Top creators table. |
| 12.6 | Implement collection name grouping view | Browse by collection (e.g., "March 2025 Patreon Drop") — filter by collectionName |

---

### WP13: Plugin Admin UI + Scheduling

**Objective:** Implement the frontend admin interface for managing plugins.

**Dependencies:** WP8, WP12

**Deliverables:**
- Plugin list view with status indicators
- Plugin configuration form (auto-generated from ConfigSchema)
- Sync trigger button and progress display
- Schedule configuration
- Auth flow initiation (for OAuth plugins)

**Estimated effort:** 3 days

**Detailed Tasks:**

| # | Task | Acceptance Criteria |
|---|------|-------------------|
| 13.1 | Implement `PluginsView.vue` | List installed plugins with: name, version, status (authenticated/needs auth/syncing/error), last sync time |
| 13.2 | Implement `PluginConfigForm.vue` | Auto-render form fields from ConfigSchema: text inputs, secret inputs (masked), numbers, booleans |
| 13.3 | Implement sync controls | "Sync Now" button per plugin, progress bar during sync, cancel button |
| 13.4 | Implement schedule config | Cron expression or interval selector per plugin |
| 13.5 | Implement auth initiation | "Authenticate" button that opens auth URL in new window, polls for completion |
| 13.6 | Implement plugin update checker | Show "Update available: v1.0 → v1.1" with update button |

---

### WP14: MCP Interface

**Objective:** Expose Forgekeeper's API as MCP (Model Context Protocol) tools for AI assistant integration.

**Dependencies:** WP5

**Deliverables:**
- MCP server with SSE transport (built-in at `/mcp`)
- Standalone MCP server option (stdio transport for local AI)
- Read tools: search, get_model, get_creator, list_sources, stats, find_duplicates, find_untagged, recent
- Write tools: tag_model, update_model, mark_printed, set_components, link_models, bulk_update, trigger_sync
- Analysis tools: collection_report, army_check, health_check, print_history

**Estimated effort:** 4 days

**Detailed Tasks:**

| # | Task | Acceptance Criteria |
|---|------|-------------------|
| 14.1 | Set up MCP server infrastructure | SSE endpoint at `/mcp`, JSON-RPC protocol handling |
| 14.2 | Implement read tools | All 8 read tools mapping to existing REST endpoints |
| 14.3 | Implement write tools | All 7 write tools with confirmation metadata |
| 14.4 | Implement analysis tools | collection_report, army_check, health_check, print_history |
| 14.5 | Implement stdio transport option | Standalone `forgekeeper-mcp` binary for local AI integrations |
| 14.6 | Add write confirmation metadata | Write tools marked with `confirmation: required`; bulk ops return preview first |
| 14.7 | Add audit logging | All MCP-initiated writes logged with `source: "mcp"` |

**MCP Tool Summary:**

| Tool | Type | Maps To |
|------|------|---------|
| `forgekeeper_search` | Read | `GET /api/v1/models` |
| `forgekeeper_get_model` | Read | `GET /api/v1/models/{id}` |
| `forgekeeper_get_creator` | Read | `GET /api/v1/creators/{id}` |
| `forgekeeper_list_sources` | Read | `GET /api/v1/sources` |
| `forgekeeper_stats` | Read | `GET /api/v1/stats` |
| `forgekeeper_find_duplicates` | Read | Custom query (name similarity + file hash) |
| `forgekeeper_find_untagged` | Read | `GET /api/v1/models?category=null` etc. |
| `forgekeeper_recent` | Read | `GET /api/v1/models?sort=date&order=desc` |
| `forgekeeper_tag_model` | Write | `POST /api/v1/models/{id}/tags` |
| `forgekeeper_update_model` | Write | `PATCH /api/v1/models/{id}` |
| `forgekeeper_mark_printed` | Write | `POST /api/v1/models/{id}/prints` |
| `forgekeeper_set_components` | Write | `PUT /api/v1/models/{id}/components` |
| `forgekeeper_link_models` | Write | Custom endpoint (new) |
| `forgekeeper_bulk_update` | Write | Batch PATCH operations |
| `forgekeeper_trigger_sync` | Write | `POST /api/v1/plugins/{slug}/sync` |
| `forgekeeper_collection_report` | Analysis | Aggregation query |
| `forgekeeper_army_check` | Analysis | Search + fuzzy match against army list text |
| `forgekeeper_health_check` | Analysis | Multi-check: orphaned files, broken refs, missing metadata |
| `forgekeeper_print_history` | Analysis | Query PrintRecord table |

---

### WP15: Testing + Documentation

**Objective:** Comprehensive test suite and developer documentation.

**Dependencies:** WP1–WP14 (parallel, continuous)

**Deliverables:**
- Unit tests for scanner logic, adapter parsing, variant detection, import rules
- Integration tests with Testcontainers PostgreSQL
- API endpoint tests
- Frontend E2E tests (optional)
- README with setup instructions
- API documentation (auto-generated via Swagger/OpenAPI)

**Estimated effort:** 5 days (spread across other WPs)

**Detailed Tasks:**

| # | Task | Acceptance Criteria |
|---|------|-------------------|
| 15.1 | Unit tests: variant detection | Test all variant detection rules from the table with sample paths |
| 15.2 | Unit tests: source adapter parsing | Test each adapter (Mmf, Patreon, Generic) with sample directory structures |
| 15.3 | Unit tests: metadata.json parsing | Test SourceMetadata deserialization with full and minimal JSON |
| 15.4 | Unit tests: import confidence scoring | Test various filename/directory patterns against expected confidence scores |
| 15.5 | Integration tests: database operations | Testcontainers PostgreSQL; test CRUD for all entities |
| 15.6 | Integration tests: search service | Test fuzzy search returns correct results, filters work, pagination correct |
| 15.7 | Integration tests: scanner service | Create temp directories with model structure, verify scanning creates correct DB records |
| 15.8 | API tests: endpoint contracts | Test all endpoints return correct status codes and response shapes |
| 15.9 | Plugin tests: ILibraryScraper mock | Test plugin host with a mock scraper implementation |
| 15.10 | Generate OpenAPI spec | Swagger UI at `/swagger` in development mode |
| 15.11 | Write README.md | Project overview, setup instructions, architecture summary, contributing guide |

**Testing Patterns:**

```csharp
// Unit test example: Variant detection
[Theory]
[InlineData("supported/model.stl", VariantType.Supported)]
[InlineData("unsupported/model.stl", VariantType.Unsupported)]
[InlineData("presupported/model.stl", VariantType.Presupported)]
[InlineData("lychee/model.lys", VariantType.LycheeProject)]
[InlineData("model.gcode", VariantType.Gcode)]
[InlineData("model.stl", VariantType.Unsupported)]  // default
public void DetectVariantType_FromPath(string path, VariantType expected)
{
    var result = VariantDetector.Detect(path);
    Assert.Equal(expected, result);
}

// Integration test example: Search
public class SearchServiceTests : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task FuzzySearch_FindsPartialMatch()
    {
        // Arrange: seed "Dragon Knight Captain" model
        // Act: search for "drago"
        // Assert: model appears in results
    }
}
```

---

### WP16: Docker + Kubernetes Deployment Manifests

**Objective:** Production-ready containerization and Kubernetes manifests for Flux GitOps deployment.

**Dependencies:** WP1, WP5, WP10

**Deliverables:**
- Multi-stage Dockerfile (SDK build → frontend build → runtime image)
- docker-compose.yml for standalone deployment
- Kubernetes manifests: Deployment, Service, Ingress, PVC, CNPG Cluster
- Flux GitOps integration in `your-flux-repo` repo
- GitHub Actions CI pipeline for building and pushing container images

**Estimated effort:** 3 days

**Detailed Tasks:**

| # | Task | Acceptance Criteria |
|---|------|-------------------|
| 16.1 | Create multi-stage Dockerfile | Stage 1: .NET SDK build. Stage 2: Node frontend build. Stage 3: runtime-deps with both outputs. Image < 200MB. |
| 16.2 | Create production docker-compose.yml | Single command `docker compose up -d` starts Forgekeeper + Postgres |
| 16.3 | Create Kubernetes manifests | Deployment, Service, Ingress (host: `forge.example.com`), NFS PVC, CNPG Cluster |
| 16.4 | Create CNPG Cluster manifest | PostgreSQL 16, 10Gi Longhorn storage, pg_trgm enabled, 256MB shared_buffers |
| 16.5 | Configure NFS PersistentVolume | Mount `/mnt/3dprinting/` as read-write PV |
| 16.6 | Create GitHub Actions CI workflow | Build Docker image on push to main, push to `ghcr.io` |
| 16.7 | Create Flux HelmRelease or Kustomization | Auto-deploy when new image tag is pushed |
| 16.8 | Configure environment variables from secrets | Connection string from CNPG secret, storage paths from ConfigMap |

**Dockerfile:**

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY *.sln .
COPY src/Forgekeeper.Api/*.csproj src/Forgekeeper.Api/
COPY src/Forgekeeper.Core/*.csproj src/Forgekeeper.Core/
COPY src/Forgekeeper.Infrastructure/*.csproj src/Forgekeeper.Infrastructure/
COPY src/Forgekeeper.PluginHost/*.csproj src/Forgekeeper.PluginHost/
COPY src/Forgekeeper.PluginSdk/*.csproj src/Forgekeeper.PluginSdk/
RUN dotnet restore
COPY . .
RUN dotnet publish src/Forgekeeper.Api -c Release -o /app

FROM node:22-alpine AS frontend
WORKDIR /app
COPY src/Forgekeeper.Web/package*.json .
RUN npm ci
COPY src/Forgekeeper.Web .
RUN npm run build

FROM mcr.microsoft.com/dotnet/aspnet:9.0
RUN apt-get update && apt-get install -y stl-thumb && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
COPY --from=frontend /app/dist wwwroot/
EXPOSE 5000
ENV ASPNETCORE_URLS=http://+:5000
ENTRYPOINT ["dotnet", "Forgekeeper.Api.dll"]
```

**Environment Variables:**

| Variable | Description | Default |
|----------|-------------|---------|
| `ConnectionStrings__ForgeDb` | PostgreSQL connection string | Required |
| `Storage__BasePaths__0` | Path to 3D printing root | `/mnt/3dprinting` |
| `Storage__ThumbnailDir` | Thumbnail output directory | `.forgekeeper/thumbnails` |
| `Scanner__FileTypes__0..N` | File extensions to index | stl,obj,3mf,lys,ctb,cbddlp,gcode |
| `Search__MinTrigramSimilarity` | pg_trgm threshold | 0.3 |
| `Search__ResultsPerPage` | Default page size | 50 |
| `Thumbnails__Enabled` | Enable thumbnail generation | true |
| `Thumbnails__Size` | Thumbnail dimensions | 256x256 |

---

### WP17: Plugin SDK NuGet Package + Template

**Objective:** Publish the Plugin SDK as a NuGet package and create a `dotnet new` template for community plugin development.

**Dependencies:** WP8

**Deliverables:**
- `Forgekeeper.PluginSdk` published to NuGet (or private feed)
- `dotnet new forgekeeper-scraper` template
- Plugin developer documentation
- Example/template scraper with all interface methods stubbed

**Estimated effort:** 2 days

**Detailed Tasks:**

| # | Task | Acceptance Criteria |
|---|------|-------------------|
| 17.1 | Configure `Forgekeeper.PluginSdk.csproj` for NuGet packaging | `dotnet pack` produces a valid .nupkg |
| 17.2 | Create template project | `dotnet new forgekeeper-scraper -n MyScraper` creates a valid project |
| 17.3 | Write plugin developer guide | Markdown doc: interface reference, lifecycle, config, testing, deployment |
| 17.4 | Create GitHub Actions workflow for plugin releases | Tag → build → publish GitHub Release with DLL |
| 17.5 | Document sibling repo development | How to use `Directory.Build.props` for simultaneous SDK + plugin development |

**Template Output:**

```
MyScraper/
├── src/
│   └── Forgekeeper.Scraper.MyScraper/
│       ├── MyScraperPlugin.cs           # ILibraryScraper implementation (stubbed)
│       ├── Forgekeeper.Scraper.MyScraper.csproj
│       └── manifest.json
├── tests/
│   └── Forgekeeper.Scraper.MyScraper.Tests/
├── Directory.Build.props
├── README.md
└── Forgekeeper.Scraper.MyScraper.sln
```

---

## 3. Data Model Reference

### Entity Definitions

#### Creator

| Column | Type | Constraints | Notes |
|--------|------|------------|-------|
| `id` | `uuid` | PK, default `gen_random_uuid()` | |
| `name` | `varchar(500)` | NOT NULL | Creator display name |
| `source` | `varchar(50)` | NOT NULL | Stored as string enum (Mmf, Thangs, etc.) |
| `source_url` | `varchar(1000)` | nullable | Profile URL on source platform |
| `external_id` | `varchar(200)` | nullable | Creator's ID on source platform |
| `avatar_url` | `varchar(1000)` | nullable | Avatar image URL |
| `model_count` | `int` | NOT NULL, default 0 | Denormalized count |
| `created_at` | `timestamp` | NOT NULL | |
| `updated_at` | `timestamp` | NOT NULL | |

**Indexes:** Unique on `(name, source)`, GIN pg_trgm on `name`

#### Model3D (table: `models`)

| Column | Type | Constraints | Notes |
|--------|------|------------|-------|
| `id` | `uuid` | PK, default `gen_random_uuid()` | |
| `creator_id` | `uuid` | FK → creators, CASCADE delete | |
| `name` | `varchar(1000)` | NOT NULL | Model display name |
| `source_id` | `varchar(200)` | nullable | External ID from source platform |
| `source` | `varchar(50)` | NOT NULL | Stored as string enum |
| `source_entity_id` | `uuid` | FK → sources, SET NULL on delete | Link to Source config entity |
| `source_url` | `varchar(1000)` | nullable | Link to model on source |
| `description` | `text` | nullable | May contain HTML from source |
| `category` | `varchar(200)` | nullable | tabletop, terrain, vehicle, prop, etc. |
| `scale` | `varchar(50)` | nullable | 28mm, 32mm, 75mm, etc. |
| `game_system` | `varchar(200)` | nullable | 40k, AoS, DnD, etc. |
| `file_count` | `int` | NOT NULL, default 0 | Denormalized |
| `total_size_bytes` | `bigint` | NOT NULL, default 0 | Denormalized |
| `thumbnail_path` | `varchar(1000)` | nullable | Path to WebP thumbnail |
| `preview_images` | `text[]` | NOT NULL, default `{}` | PostgreSQL array of image paths |
| `base_path` | `varchar(2000)` | NOT NULL, UNIQUE | Filesystem path to model directory |
| `rating` | `int` | nullable | User rating 1-5 |
| `notes` | `text` | nullable | User notes |
| `extra` | `jsonb` | nullable | Raw `extra` from metadata.json |
| `print_history` | `jsonb` | nullable | Array of PrintHistoryEntry objects |
| `components` | `jsonb` | nullable | Array of ComponentInfo objects |
| `license_type` | `varchar(100)` | nullable | Denormalized from metadata.json license.type |
| `collection_name` | `varchar(500)` | nullable | Denormalized from metadata.json collection.name |
| `external_created_at` | `timestamp` | nullable | dates.created from metadata.json |
| `external_updated_at` | `timestamp` | nullable | dates.updated from metadata.json |
| `downloaded_at` | `timestamp` | nullable | dates.downloaded from metadata.json |
| `created_at` | `timestamp` | NOT NULL | |
| `updated_at` | `timestamp` | NOT NULL | |
| `last_scanned_at` | `timestamp` | nullable | Last time scanner processed this model |

**Indexes:** Unique on `base_path`, GIN pg_trgm on `name`, btree on `creator_id`, `source`, `category` (partial), `game_system` (partial), `license_type`, `collection_name`, GIN jsonb_path_ops on `extra`

**Note:** `printed` is a computed property on the C# model: `PrintHistory?.Any(p => p.Result == "success")`. It is NOT a database column. Use `entity.Ignore(e => e.Printed)` in EF configuration.

#### Variant (table: `variants`)

| Column | Type | Constraints | Notes |
|--------|------|------------|-------|
| `id` | `uuid` | PK, default `gen_random_uuid()` | |
| `model_id` | `uuid` | FK → models, CASCADE delete | |
| `variant_type` | `varchar(50)` | NOT NULL | Stored as string enum |
| `file_path` | `varchar(2000)` | NOT NULL, UNIQUE | Relative to base_path or absolute |
| `file_name` | `varchar(500)` | NOT NULL | Just the filename |
| `file_type` | `varchar(50)` | NOT NULL | Stored as string enum |
| `file_size_bytes` | `bigint` | NOT NULL | |
| `thumbnail_path` | `varchar(1000)` | nullable | Per-variant thumbnail (optional) |
| `file_hash` | `varchar(200)` | nullable | SHA-256 hash for dedup |
| `physical_properties` | `jsonb` | nullable | BoundingBox, triangle count, etc. |
| `created_at` | `timestamp` | NOT NULL | |

**Indexes:** Unique on `file_path`, btree on `model_id`

#### Tag (table: `tags`)

| Column | Type | Constraints | Notes |
|--------|------|------------|-------|
| `id` | `uuid` | PK, default `gen_random_uuid()` | |
| `name` | `varchar(200)` | NOT NULL, UNIQUE | Lowercase, normalized |

#### model_tags (junction table)

| Column | Type | Constraints |
|--------|------|------------|
| `model_id` | `uuid` | FK → models, CASCADE delete |
| `tag_id` | `uuid` | FK → tags, CASCADE delete |
| | | PK on `(model_id, tag_id)` |

#### Source (table: `sources`)

| Column | Type | Constraints | Notes |
|--------|------|------------|-------|
| `id` | `uuid` | PK, default `gen_random_uuid()` | |
| `slug` | `varchar(100)` | NOT NULL, UNIQUE | URL-safe directory name (mmf, thangs, etc.) |
| `name` | `varchar(500)` | NOT NULL | Human-readable display name |
| `base_path` | `varchar(2000)` | NOT NULL | Filesystem path to source directory |
| `adapter_type` | `varchar(200)` | NOT NULL, default 'GenericSourceAdapter' | Adapter class name |
| `auto_scan` | `bool` | NOT NULL, default true | Include in periodic scans |
| `created_at` | `timestamp` | NOT NULL | |
| `updated_at` | `timestamp` | NOT NULL | |

#### ScanState (table: `scan_states`)

| Column | Type | Constraints | Notes |
|--------|------|------------|-------|
| `id` | `uuid` | PK, default `gen_random_uuid()` | |
| `directory_path` | `varchar(2000)` | NOT NULL, UNIQUE | |
| `last_scanned_at` | `timestamp` | NOT NULL | |
| `last_modified_at` | `timestamp` | NOT NULL | Filesystem mtime at last scan |
| `file_count` | `int` | NOT NULL | Files in directory at last scan |

#### ImportQueueItem (table: `import_queue`)

| Column | Type | Constraints | Notes |
|--------|------|------------|-------|
| `id` | `uuid` | PK, default `gen_random_uuid()` | |
| `original_path` | `varchar(2000)` | NOT NULL | Path in unsorted/ |
| `detected_creator` | `varchar(500)` | nullable | Auto-detected creator name |
| `detected_model_name` | `varchar(1000)` | nullable | Auto-detected model name |
| `detected_source` | `varchar(50)` | nullable | Auto-detected source |
| `detected_variant_type` | `varchar(50)` | nullable | |
| `confidence_score` | `double` | NOT NULL | 0.0 - 1.0 |
| `status` | `varchar(50)` | NOT NULL | Pending, AutoSorted, AwaitingReview, Confirmed, Failed |
| `confirmed_creator` | `varchar(500)` | nullable | User-confirmed |
| `confirmed_model_name` | `varchar(1000)` | nullable | User-confirmed |
| `confirmed_source` | `varchar(50)` | nullable | User-confirmed |
| `error_message` | `text` | nullable | |
| `created_at` | `timestamp` | NOT NULL | |
| `updated_at` | `timestamp` | NOT NULL | |

**Indexes:** btree on `status`

### metadata.json Schema Reference (v1)

**Required fields (minimum viable):**

```json
{
  "metadataVersion": 1,
  "source": "your-source-slug",
  "externalId": "unique-id",
  "name": "Model Name",
  "dates": { "downloaded": "2026-04-15T18:00:00Z" }
}
```

**Complete schema (all fields):**

```json
{
  "metadataVersion": 1,
  "source": "mmf",
  "externalId": "123456",
  "externalUrl": "https://www.myminifactory.com/object/123456",
  "name": "Space Marine Captain with Power Sword",
  "description": "A highly detailed 32mm miniature...",
  "type": "object",
  "tags": ["warhammer", "40k", "space marine"],
  "creator": {
    "externalId": "789012",
    "username": "AwesomeSculptor",
    "displayName": "Awesome Sculptor Studio",
    "avatarUrl": "https://cdn.example.com/avatar.jpg",
    "profileUrl": "https://www.myminifactory.com/users/AwesomeSculptor"
  },
  "dates": {
    "created": "2025-06-15T10:30:00Z",
    "updated": "2025-08-20T14:00:00Z",
    "published": "2025-06-15T12:00:00Z",
    "addedToLibrary": "2025-07-01T00:00:00Z",
    "downloaded": "2026-04-15T18:00:00Z"
  },
  "acquisition": {
    "method": "purchase",
    "orderId": "F51015845C",
    "campaignId": null
  },
  "license": {
    "type": "personal",
    "text": "For personal use only.",
    "url": null
  },
  "collection": {
    "name": "Dragon Army Bundle - March 2025",
    "externalId": "bundle-3147",
    "index": 3,
    "total": 12,
    "url": null
  },
  "sourceRating": {
    "score": 4.8,
    "maxScore": 5.0,
    "votes": 127,
    "downloads": 5420,
    "likes": 892
  },
  "printSettings": {
    "technology": "resin",
    "layerHeight": 0.05,
    "scale": "32mm",
    "supportsRequired": true,
    "estimatedPrintTime": "4h 30m",
    "estimatedResin": "45ml",
    "notes": "Print at 45 degree angle"
  },
  "relatedModels": [
    { "externalId": "67890", "name": "Dragon Knight Mount", "relation": "companion" }
  ],
  "components": [
    { "name": "Body", "file": "unsupported/body.stl", "required": true, "group": null },
    { "name": "Power Sword", "file": "unsupported/power_sword.stl", "required": false, "group": "weapon" },
    { "name": "Thunder Hammer", "file": "unsupported/thunder_hammer.stl", "required": false, "group": "weapon" }
  ],
  "images": [
    { "url": "https://cdn.example.com/preview.jpg", "localPath": "images/preview.jpg", "type": "gallery" }
  ],
  "files": [
    {
      "filename": "captain_supported.stl",
      "originalFilename": "SM_Captain_V2_FINAL_supported (1).stl",
      "localPath": "supported/captain_supported.stl",
      "size": 45678901,
      "variant": "supported",
      "downloadedAt": "2026-04-15T18:00:05Z"
    }
  ],
  "fileHashes": {
    "supported/captain_supported.stl": "sha256:a1b2c3d4e5f6..."
  },
  "extra": {}
}
```

### Database Migration Strategy

1. **Initial migration (WP2):** Creates all tables, indexes, extensions (`pg_trgm`)
2. **Subsequent migrations:** Use `dotnet ef migrations add <Name>` in the Infrastructure project with the Api project as startup
3. **Production:** Apply migrations on startup via `context.Database.MigrateAsync()` or manually via `dotnet ef database update`
4. **Rollback:** EF Core generates `Down()` methods automatically; test rollbacks before production deploy
5. **Seeding:** Initial sources (mmf, thangs, patreon, cults3d, thingiverse, manual) seeded in migration or on first startup

**Migration Command:**

```bash
docker exec forgekeeper-dev dotnet ef migrations add <MigrationName> \
  --project src/Forgekeeper.Infrastructure \
  --startup-project src/Forgekeeper.Api
```

---

## 4. API Contract

See §WP5 above for the complete endpoint list. Key additional details:

### Authentication Model

**None.** Forgekeeper is a single-user application deployed on a private network behind Kubernetes ingress. No authentication middleware is configured. Future: add Keycloak OIDC if multi-user or external access is needed.

### Pagination Pattern

All list endpoints use the same pagination pattern:

**Request:** `?page=1&pageSize=50`

**Response:**

```json
{
  "items": [...],
  "totalCount": 7223,
  "page": 1,
  "pageSize": 50,
  "totalPages": 145,
  "hasNext": true,
  "hasPrevious": false
}
```

**Defaults:** page=1, pageSize=50, max pageSize=200

### Sorting Pattern

**Request:** `?sortBy=name&sortDescending=false`

**Valid sort fields:** `name`, `date` (updatedAt), `size` (totalSizeBytes), `rating`, `fileCount`

### Error Response Format

```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.5.4",
  "title": "Not Found",
  "status": 404,
  "detail": "Model with ID '...' was not found."
}
```

---

## 5. Frontend Component Specification

### Page Layouts

| Route | View | Description |
|-------|------|-------------|
| `/` | `BrowseView.vue` | Grid of ModelCards with SearchBar + FilterSidebar |
| `/models/:id` | `ModelDetailView.vue` | Full model page with variants, 3D viewer, metadata editor |
| `/creators` | `CreatorsList.vue` | Creator directory with search |
| `/creators/:id` | `CreatorDetailView.vue` | Creator page with model grid |
| `/import` | `ImportQueueView.vue` | Import review queue |
| `/stats` | `StatsView.vue` | Collection statistics dashboard |
| `/plugins` | `PluginsView.vue` | Plugin management admin |

### Component Props/Events/State

#### `ModelCard.vue`

| Prop | Type | Description |
|------|------|-------------|
| `model` | `ModelResponse` | Model data object |

| Event | Payload | Description |
|-------|---------|-------------|
| `click` | `model.id` | Navigate to model detail |

#### `StlViewer.vue`

| Prop | Type | Description |
|------|------|-------------|
| `url` | `string` | URL to download STL file |
| `backgroundColor` | `string` | Scene background color (default: `#1a1a2e`) |
| `modelColor` | `string` | Mesh material color (default: `#4ecca3`) |

| Event | Payload | Description |
|-------|---------|-------------|
| `loaded` | `{ triangles: number }` | Emitted when STL finishes loading |
| `error` | `Error` | Emitted on load failure |

**Internal state:** THREE.Scene, Camera, Renderer, Controls — managed by `useViewer` composable. Cleaned up on `onUnmounted`.

#### `TagEditor.vue`

| Prop | Type | Description |
|------|------|-------------|
| `modelId` | `string` | Model UUID |
| `tags` | `string[]` | Current tags |

| Event | Payload | Description |
|-------|---------|-------------|
| `add` | `string` | Tag name added |
| `remove` | `string` | Tag name removed |

#### `FilterSidebar.vue`

| Prop | Type | Description |
|------|------|-------------|
| `filters` | `ModelSearchRequest` | Current filter state |

| Event | Payload | Description |
|-------|---------|-------------|
| `update` | `Partial<ModelSearchRequest>` | Filter changed |
| `reset` | — | Clear all filters |

#### `SearchBar.vue`

| Prop | Type | Description |
|------|------|-------------|
| `value` | `string` | Current search text |
| `debounceMs` | `number` | Debounce delay (default: 300) |

| Event | Payload | Description |
|-------|---------|-------------|
| `search` | `string` | Debounced search text |

### Three.js Viewer Requirements

- **Loader:** `THREE.STLLoader` from `three/examples/jsm/loaders/STLLoader`
- **Controls:** `OrbitControls` from `three/examples/jsm/controls/OrbitControls`
- **Scene setup:**
  - Background: `#1a1a2e` (dark blue)
  - Ambient light: `0x404040`, intensity 2
  - Directional light: `0xffffff`, intensity 1.5, position (1, 1, 1)
- **Material:** `MeshPhongMaterial({ color: 0x4ecca3, specular: 0x111111, shininess: 200 })`
- **Camera auto-fit:** Calculate bounding sphere of loaded geometry, position camera at 2× sphere radius
- **Loading indicator:** Spinner overlay while STL downloads
- **Error handling:** Display error message if STL fails to load (corrupt file, too large, network error)
- **Responsive:** Canvas resizes with container; use ResizeObserver
- **Performance:** Lazy-load Three.js only when StlViewer is mounted (code splitting)

---

## 6. Plugin System Specification

### ILibraryScraper Interface (Complete C#)

```csharp
namespace Forgekeeper.PluginSdk;

/// <summary>
/// Contract for a Library Scraper plugin.
/// Implement this to add support for a new 3D model source.
/// </summary>
public interface ILibraryScraper
{
    /// <summary>
    /// Unique slug identifying this source (e.g., "mmf", "thangs", "patreon").
    /// Used as the source directory name and metadata.json source field.
    /// Must be lowercase, URL-safe, no spaces.
    /// </summary>
    string SourceSlug { get; }
    
    /// <summary>Human-readable name (e.g., "MyMiniFactory", "Thangs")</summary>
    string SourceName { get; }
    
    /// <summary>Description of what this scraper does</summary>
    string Description { get; }
    
    /// <summary>Semantic version of this plugin</summary>
    string Version { get; }
    
    /// <summary>
    /// Configuration schema — what settings this plugin needs.
    /// Displayed in the Forgekeeper admin UI as a form.
    /// </summary>
    IReadOnlyList<PluginConfigField> ConfigSchema { get; }
    
    /// <summary>
    /// Whether this plugin requires browser-based auth (OAuth, cookies).
    /// If true, Forgekeeper provides a Playwright browser context.
    /// </summary>
    bool RequiresBrowserAuth { get; }
    
    /// <summary>
    /// Authenticate with the source platform.
    /// Called at startup and when token expires.
    /// Return AuthResult.Authenticated=true if ready.
    /// Return AuthResult.AuthUrl if user needs to visit a URL.
    /// </summary>
    Task<AuthResult> AuthenticateAsync(PluginContext context, CancellationToken ct);
    
    /// <summary>
    /// Fetch the library manifest (list of available models).
    /// Called on schedule or when user triggers "Refresh Library".
    /// </summary>
    Task<IReadOnlyList<ScrapedModel>> FetchManifestAsync(PluginContext context, CancellationToken ct);
    
    /// <summary>
    /// Download files for a specific model.
    /// Write files to context.ModelDirectory. Return metadata and file list.
    /// Forgekeeper handles: metadata.json writing, ZIP extraction, version cleanup.
    /// </summary>
    Task<ScrapeResult> ScrapeModelAsync(ScrapedModel model, PluginContext context, CancellationToken ct);
    
    /// <summary>
    /// Handle OAuth callback. Forgekeeper routes /auth/{sourceSlug}/callback here.
    /// </summary>
    Task<bool> HandleAuthCallbackAsync(HttpContext httpContext, PluginContext context);
    
    /// <summary>
    /// Optional: provide custom HTML for the admin page.
    /// Rendered as an iframe in Forgekeeper's plugin admin panel.
    /// </summary>
    string? GetAdminPageHtml(PluginContext context) => null;
}
```

### Plugin Lifecycle

```
1. DISCOVERY (startup)
   └─ Scan plugins/ for DLLs → Load AssemblyLoadContext → Find ILibraryScraper types

2. CONFIGURATION
   └─ Load config from DB → Call InitializeAsync (implicit via PluginContext) → Plugin ready

3. AUTHENTICATION
   └─ Call AuthenticateAsync()
      ├─ Authenticated=true → proceed to sync
      └─ Authenticated=false, AuthUrl set → show "Authenticate" button in UI
         └─ User visits AuthUrl → OAuth flow → /auth/{slug}/callback → HandleAuthCallbackAsync()
         └─ Token stored via context.TokenStore

4. SYNC (scheduled or manual)
   └─ Call FetchManifestAsync() → get List<ScrapedModel>
   └─ For each new/updated model:
      ├─ Create model directory at sources/{slug}/{creator}/{model}/
      ├─ Set context.ModelDirectory
      ├─ Call ScrapeModelAsync() → plugin downloads files
      ├─ Forgekeeper writes metadata.json from ScrapeResult
      ├─ If DownloadedFile.IsArchive=true → Forgekeeper extracts ZIP
      └─ Trigger scanner for new directory

5. UPDATE CHECK (daily)
   └─ Read manifest.json repository URL
   └─ Check GitHub Releases for new version
   └─ Show "Update available" in admin UI
```

### Plugin Config Schema

```csharp
public class PluginConfigField
{
    public string Key { get; set; }          // e.g., "CLIENT_ID"
    public string Label { get; set; }        // e.g., "OAuth Client ID"
    public string Type { get; set; }         // "string", "secret", "url", "number", "bool"
    public string? DefaultValue { get; set; }
    public bool Required { get; set; }
    public string? HelpText { get; set; }    // Shown as tooltip in admin UI
}
```

### Testing a Plugin

1. Create the plugin project referencing `Forgekeeper.PluginSdk`
2. Build the DLL: `dotnet build -c Release`
3. Copy DLL to Forgekeeper's `plugins/` directory
4. Restart Forgekeeper → plugin appears in admin UI
5. Configure via admin UI → authenticate → trigger sync
6. Verify models appear in Forgekeeper's browse view

**Unit testing without Forgekeeper:**

```csharp
[Fact]
public async Task FetchManifest_ReturnsModels()
{
    var plugin = new MyScraperPlugin();
    var context = new PluginContext
    {
        Config = new Dictionary<string, string> { ["API_KEY"] = "test-key" },
        HttpClient = new HttpClient(), // or mock
        Logger = NullLogger.Instance,
        // ...
    };
    var models = await plugin.FetchManifestAsync(context, CancellationToken.None);
    Assert.NotEmpty(models);
}
```

---

## 7. metadata.json Field Ownership Matrix

This matrix defines who writes each field and how conflicts are resolved when both a scraper and Forgekeeper touch the same metadata.json.

| Field | Writer | Reader | Merge Behavior |
|-------|--------|--------|---------------|
| `metadataVersion` | Scraper | Forgekeeper | Scraper sets; Forgekeeper validates |
| `source` | Scraper | Forgekeeper | **Scraper owns** — Forgekeeper MUST NOT write |
| `externalId` | Scraper | Forgekeeper | **Scraper owns** — Forgekeeper MUST NOT write |
| `externalUrl` | Scraper | Forgekeeper | **Scraper owns** |
| `name` | Scraper | Forgekeeper | **Scraper owns** |
| `description` | Scraper | Forgekeeper | **Scraper owns** |
| `type` | Scraper | Forgekeeper | **Scraper owns** |
| `tags` | **Both** | Both | **Union merge** — both add, neither removes |
| `creator.*` | Scraper | Forgekeeper | **Scraper owns** |
| `dates.created` | Scraper | Forgekeeper | **Scraper owns** |
| `dates.updated` | Scraper | Forgekeeper | **Scraper owns** |
| `dates.published` | Scraper | Forgekeeper | **Scraper owns** |
| `dates.addedToLibrary` | Scraper | Forgekeeper | **Scraper owns** |
| `dates.downloaded` | Scraper | Forgekeeper | **Scraper owns** |
| `acquisition` | Scraper | Forgekeeper | **Scraper owns** |
| `license` | **Both** | Both | **User-set value wins** — if user sets in Forgekeeper, it overrides source value |
| `collection` | Forgekeeper | Both | Forgekeeper writes; scraper preserves on re-sync (opaque round-trip) |
| `sourceRating` | Scraper | Forgekeeper | **Scraper owns** |
| `printSettings` | Forgekeeper | Forgekeeper | Scraper preserves on re-sync (opaque round-trip) |
| `printHistory` | Forgekeeper | Forgekeeper | Written back for database-free recovery; scraper preserves (opaque round-trip) |
| `relatedModels` | Forgekeeper | Forgekeeper | Scraper preserves (opaque round-trip) |
| `components` | Forgekeeper | Both | Scraper preserves (opaque round-trip) |
| `images` | Scraper | Forgekeeper | **Scraper owns** |
| `files` | Scraper | Forgekeeper | **Scraper owns** |
| `fileHashes` | Scraper | Forgekeeper | **Scraper owns** (Forgekeeper may compute as fallback) |
| `physicalProperties` | Forgekeeper | Forgekeeper | Computed during scanning; scraper preserves (opaque round-trip) |
| `extra` | Scraper | Forgekeeper | **Scraper owns** — opaque to Forgekeeper, preserved verbatim |

**"Opaque round-trip"** means: when MMF scraper (or any scraper) re-syncs, it stores Forgekeeper-owned fields as raw `JsonElement` values — it doesn't deserialize or interpret them, just copies them through the merge. This allows Forgekeeper to evolve these fields freely without requiring scraper updates.

---

## 8. Testing Strategy

### Unit Tests

| Service/Component | What to Test |
|-------------------|-------------|
| **Variant detection** | All folder patterns → VariantType mapping (see variant detection rules table) |
| **Source adapters** | Each adapter correctly parses directory structure into ParsedModelInfo |
| **metadata.json parsing** | Full schema, minimal schema, missing optional fields, malformed JSON |
| **Import confidence scoring** | Various filename patterns produce expected scores |
| **File type detection** | Extension → FileType enum mapping for all supported types |
| **Search request validation** | Invalid page, oversized pageSize, invalid sort field |
| **Model update** | PATCH with partial fields only updates specified fields |

### Integration Tests (Testcontainers PostgreSQL)

| Test Area | What to Test |
|-----------|-------------|
| **Repository CRUD** | Create, read, update, delete for all entities |
| **Search service** | Fuzzy search accuracy, filter combinations, pagination, sorting |
| **Scanner service** | Create temp directories → scan → verify correct DB records created |
| **Import service** | Drop files in temp unsorted/ → process → verify queue items created |
| **Tag management** | Add/remove tags, verify M2M relationship integrity |
| **Print history** | Add print entries, verify Printed computed property |
| **pg_trgm** | Verify trigram indexes work, similarity threshold respected |

### API Tests

| Endpoint Group | What to Test |
|---------------|-------------|
| **Models** | GET list (with filters), GET detail, PATCH update, DELETE, POST prints, PUT components |
| **Creators** | GET list, GET detail with stats, GET creator's models |
| **Variants** | GET download (file stream), GET thumbnail |
| **Tags** | GET all, POST add, DELETE remove |
| **Scan** | POST trigger, GET status |
| **Stats** | GET stats, GET creator stats |
| **Sources** | CRUD operations |
| **Import** | POST process, GET queue, POST confirm, DELETE dismiss |

### E2E Tests (Optional)

| Flow | What to Test |
|------|-------------|
| **Browse → Detail** | Load browse page, click model, verify detail page loads with correct data |
| **Search** | Type search query, verify results update, apply filters |
| **Tag management** | Add tag in detail view, verify it appears in browse filters |
| **Import flow** | Drop test files, process, review, confirm, verify model appears in browse |

### Plugin Testing

```csharp
public class MockScraper : ILibraryScraper
{
    public string SourceSlug => "test";
    public string SourceName => "Test Source";
    // ... stub all methods with predictable test data

    public Task<IReadOnlyList<ScrapedModel>> FetchManifestAsync(...)
        => Task.FromResult<IReadOnlyList<ScrapedModel>>(new List<ScrapedModel>
        {
            new() { ExternalId = "1", Name = "Test Model", CreatorName = "Test Creator" }
        });
}
```

---

## 9. Deployment Checklist

### Docker Build Steps

```bash
# Build the production image
docker build -t ghcr.io/forgekeeper/forgekeeper:latest .

# Push to registry
docker push ghcr.io/forgekeeper/forgekeeper:latest
```

### Kubernetes Manifests

Located in `your-flux-repo` GitOps repo under `apps/forgekeeper/`:

| Manifest | Purpose |
|----------|---------|
| `namespace.yaml` | `forgekeeper` namespace |
| `deployment.yaml` | Single replica, NFS volume mount, env vars from secrets |
| `service.yaml` | ClusterIP on port 5000 |
| `ingress.yaml` | Host: `forge.example.com` |
| `cnpg-cluster.yaml` | PostgreSQL 16 via CNPG, 10Gi Longhorn, pg_trgm |
| `nfs-pv.yaml` | PersistentVolume for your 3D printing collection |
| `nfs-pvc.yaml` | PersistentVolumeClaim for the NFS PV |
| `configmap.yaml` | Non-secret configuration (storage paths, scanner config) |
| `secret.yaml` | Sealed secret for DB credentials |

### Environment Variables Reference

| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `ConnectionStrings__ForgeDb` | ✅ | — | PostgreSQL connection string |
| `Storage__BasePaths__0` | ✅ | `/mnt/3dprinting` | Root path to 3D printing collection |
| `Storage__ThumbnailDir` | | `.forgekeeper/thumbnails` | Thumbnail output directory |
| `Scanner__FileTypes__0` through `__N` | | stl,obj,3mf,lys,ctb,cbddlp,gcode,sl1 | Extensions to index |
| `Scanner__ImageTypes__0` through `__N` | | png,jpg,jpeg,webp | Image extensions |
| `Scanner__IgnorePatterns__0` through `__N` | | *.tmp,.DS_Store,Thumbs.db | Files to skip |
| `Search__MinTrigramSimilarity` | | 0.3 | pg_trgm match threshold |
| `Search__ResultsPerPage` | | 50 | Default page size |
| `Thumbnails__Enabled` | | true | Enable thumbnail generation |
| `Thumbnails__Size` | | 256x256 | Thumbnail dimensions |
| `Thumbnails__Format` | | webp | Output format |
| `Thumbnails__Renderer` | | stl-thumb | Rendering tool |
| `ASPNETCORE_ENVIRONMENT` | | Production | .NET environment |
| `ASPNETCORE_URLS` | | http://+:5000 | Listening URLs |

### First-Run Experience

1. Deploy Forgekeeper + PostgreSQL
2. EF migrations auto-apply on startup (or run manually)
3. Default sources created: mmf, thangs, patreon, cults3d, thingiverse, manual (pointing to `{basePath}/sources/{slug}/`)
4. Navigate to `forge.example.com`
5. Go to Sources page → configure base paths if different from defaults
6. Trigger initial full scan → monitor progress at `/scan/status`
7. Browse begins populating as scan progresses
8. Thumbnail generation starts automatically in background
9. Configure plugins (if any scrapers installed)

---

## 10. Glossary

| Term | Definition |
|------|-----------|
| **Source** | Where files came from — a platform like MyMiniFactory, Thangs, Patreon, Cults3D, Thingiverse, or "manual" for hand-organized files. Each source has its own top-level directory under `sources/`. |
| **Creator** | The person or studio who designed the 3D models. Exists within a source (e.g., "AwesomeSculptor" on MMF). May appear on multiple sources. |
| **Model** (`Model3D`) | A single 3D printable design — a miniature, terrain piece, prop, or functional part. Contains one or more Variants. |
| **Variant** | A specific version of a model file. Types: unsupported (raw mesh), supported (FDM supports), presupported (resin supports), lychee (slicer project), other. A model may have 15+ variants (body, weapons, heads, bases). |
| **metadata.json** | Sidecar JSON file in each model directory that carries source information, creator details, dates, tags, license info, and file manifests. The "ID3 tag for STL files." Integration contract between downloaders and Forgekeeper. |
| **Source Adapter** (`ISourceAdapter`) | A class that understands a specific source's directory layout and can parse it into Forgekeeper's canonical model. Not the same as a scraper — adapters only read existing directories. |
| **Library Scraper** (`ILibraryScraper`) | A plugin that downloads files from an external source. Scrapers are active (they fetch data); adapters are passive (they read directories). |
| **Plugin Host** | Forgekeeper's infrastructure for discovering, loading, configuring, and running scraper plugins. |
| **Unsorted** (`unsorted/`) | Drop zone directory where users dump raw files (ZIPs, loose STLs, folders). The Import Service processes these into canonical source directories. |
| **Import Queue** | List of items detected in `unsorted/` that need user review before being sorted into the collection. |
| **Canonical Layout** | The standard directory structure: `sources/{source}/{creator}/{model}/{variant}/`. All sources are normalized into this layout. |
| **Scan State** | Per-directory tracking of when Forgekeeper last scanned it and what the filesystem mtime was. Enables incremental scanning. |
| **pg_trgm** | PostgreSQL extension for trigram-based fuzzy text search. Powers Forgekeeper's search without needing Elasticsearch. |
| **CNPG** | CloudNativePG — Kubernetes operator for managing PostgreSQL clusters. Used in Forgekeeper's k8s deployment. |
| **MMF scraper** | Legacy standalone tool for downloading MyMiniFactory libraries. Replaced by the MMF scraper plugin. Output directory structure is Forgekeeper's `sources/mmf/` canonical layout. |
| **Opaque Round-Trip** | When a scraper encounters Forgekeeper-owned fields in metadata.json during re-sync, it preserves them as raw JSON without interpreting them. Allows Forgekeeper to evolve fields freely. |
| **PrintRecord** | An entry in a model's print history tracking a print attempt: date, printer, material, result (success/failed/partial), notes. |
| **Component** | A part of a multi-piece model with option groups. E.g., "Body" (required) + "Power Sword" or "Thunder Hammer" (weapon group — pick one). |
| **Collection** | A group of related models — Patreon monthly drop, MMF bundle, Kickstarter campaign. Tracked via `collection` field in metadata.json. |

---

## 11. Consistency Review

After reviewing the implementation plan against all source documents (Spec, Architecture, Dev Environment, Library Scraper Plugin, MMF scraper), the following discrepancies, gaps, and ambiguities are flagged:

### Fields in Spec Not Fully Covered

| Issue | Source | Resolution Needed |
|-------|--------|-------------------|
| `physicalProperties` (bounding box, triangle count, watertight, volume) is defined in the spec as computed by Forgekeeper during scanning | Spec §Additional Metadata Fields | **Gap:** No WP explicitly covers implementing mesh analysis. The scanning WP (WP3) should include this or defer to a separate WP. Currently only stored as JSONB on Variant; no computation logic specified. **Recommend:** Defer to Phase 3 — requires STL parsing library (e.g., `geometry3Sharp` NuGet). |
| `printHistory` — spec says "the `printed` boolean becomes a computed property" but also mentions writing `printHistory` back to metadata.json for portability | Spec §printHistory | **Covered** in the data model (computed `Printed` property), but **writing printHistory back to metadata.json** is not in any WP. **Recommend:** Add to WP3 or a new sub-task — when print history changes, update metadata.json on disk. |
| `sourceRating` field in metadata.json | Spec §sourceRating | **Not stored in database.** The field is read from metadata.json but has no corresponding column on Model3D. **Recommend:** Store as part of `Extra` JSONB or add dedicated column if filtering by source rating is desired. |
| `relatedModels` field in metadata.json | Spec §relatedModels | **Not stored in database** as a dedicated relationship. Stored in metadata.json only. The MCP interface references `forgekeeper_link_models` but no DB table exists for model relationships. **Recommend:** Add a `model_relations` junction table or store as JSONB on Model3D. |
| `printSettings` field in metadata.json | Spec §printSettings | **Not stored in database.** Read from metadata.json but no corresponding columns. Acceptable if only accessed via metadata.json on disk, but means no search/filter by print settings. |

### Endpoints in API Design Not Included

| Endpoint | Source | Status |
|----------|--------|--------|
| Bulk operations (tag multiple models, set category for entire creator) | Spec §Phase 2, Feature 7 | **Not in API contract.** Need: `POST /api/v1/models/bulk` with operation + modelIds[] |
| `GET /api/v1/models/{id}/thumbnail` (model-level thumbnail) | Implied by frontend | **Missing.** Currently only variant-level thumbnail endpoint exists. **Recommend:** Add model thumbnail endpoint that returns the first variant's thumbnail or a dedicated model thumbnail. |
| `POST /api/v1/models/{id}/related` (link related models) | MCP tool `forgekeeper_link_models` | **Missing from REST API.** MCP references this but no endpoint defined. |
| `GET /api/v1/models/duplicates` (duplicate detection) | MCP tool `forgekeeper_find_duplicates` | **Missing.** Needed for both MCP and future UI. |
| Plugin admin endpoints | WP8 | **Defined in WP8** but not in the main API contract section. Should be documented. |

### metadata.json Fields Missing from Data Model

| Field | In metadata.json | In Database | Notes |
|-------|-----------------|-------------|-------|
| `sourceRating` | ✅ | ❌ | No column or JSONB storage |
| `relatedModels` | ✅ | ❌ | No junction table or JSONB |
| `printSettings` | ✅ | ❌ | No columns |
| `physicalProperties` | ✅ | ✅ (on Variant as JSONB) | Variant-level but not Model-level |
| `acquisition` | ✅ | ❌ | No columns (AcquisitionMethod enum exists but unused on Model3D) |
| `dates.addedToLibrary` | ✅ | ❌ | No corresponding column |
| `dates.published` | ✅ | ❌ | No corresponding column |
| `files[].originalFilename` | ✅ | ❌ | Not stored on Variant entity |
| `images[].url` | ✅ | Partial | Only `PreviewImages` (local paths), not source URLs |

### Features Mentioned in One Doc But Not Another

| Feature | Mentioned In | Not In | Resolution |
|---------|-------------|--------|------------|
| **Slicer integration** ("Send to Bambu Lab/OctoPrint") | Spec §Phase 3, Architecture Roadmap Phase 4 | Implementation plan | **Intentionally deferred** — not in any WP. Future feature. |
| **Duplicate detection** (hash-based cross-source) | Spec §Phase 3, Architecture Roadmap Phase 3, MCP tools | No dedicated WP | **Gap.** Add to WP15 or create WP18. Requires `Variant.FileHash` to be populated during scanning. |
| **Creator merging across sources** | Spec §Phase 2, Architecture §Core Concepts | No dedicated task | **Gap.** When same creator publishes on MMF + Patreon, they appear as separate creators. Need a merge UI/API. Defer to Phase 2. |
| **File watching (inotify)** | Spec §Phase 2 Feature 8 | No WP | **Intentionally deferred.** Periodic rescan handles this for now. inotify doesn't work well on NFS anyway. |
| **Game system auto-detection** | Spec §Open Questions, Architecture Roadmap Phase 4 | No WP | **Deferred.** Could use creator/model name heuristics ("Space Marine" → 40k) but not in scope for MVP. |
| **ThingiverseSourceAdapter** | Spec §Source Adapters table | Not in existing code (no `ThingiverseSourceAdapter.cs`) | **Gap.** Spec mentions it; code only has Mmf, Patreon, Generic adapters. Thingiverse may need its own adapter if Thing IDs are in folder names. Otherwise `GenericSourceAdapter` suffices. |
| **Playwright browser context** | Library Scraper Plugin §ILibraryScraper | Not in existing code | **Expected** — the PluginHost needs to optionally provide Playwright for OAuth-heavy sources like MMF. |

### Version/Naming Inconsistencies

| Inconsistency | Documents | Resolution |
|---------------|-----------|------------|
| Frontend project naming: `Forgekeeper.Web` (existing code, spec) vs `Forgekeeper.Frontend` (architecture doc) | Spec uses `Forgekeeper.Web`, Architecture uses `Forgekeeper.Frontend` | **Use `Forgekeeper.Web`** — matches existing code |
| `SourceName` (Library Scraper Plugin interface) vs `DisplayName` (Architecture doc interface) | Library Scraper Plugin §ILibraryScraper vs Architecture §Plugin Architecture | **Use `SourceName`** — matches the more detailed Library Scraper Plugin doc |
| `rating` field name in metadata.json: `"rating"` in SourceMetadata.cs vs `"sourceRating"` in spec/architecture | SourceMetadata.cs uses `[JsonPropertyName("rating")]` | **Bug in existing code.** Should be `"sourceRating"` per spec. Fix the JsonPropertyName. |
| `ConfigField` (Architecture) vs `PluginConfigField` (Library Scraper Plugin) | Different naming | **Use `PluginConfigField`** — more specific, matches Library Scraper Plugin doc |
| Architecture doc mentions `Version` as `Version` type on ILibraryScraper; Library Scraper Plugin uses `string Version` | Different return types | **Use `string`** — simpler, matches Library Scraper Plugin doc |
| Legacy downloader used .NET 8; spec and architecture say .NET 9 for Forgekeeper | Different target frameworks | **Forgekeeper is .NET 9.** Legacy downloader was .NET 8. The MMF scraper plugin will target .NET 9. |

### Ambiguities That Need Resolution

| # | Ambiguity | Context | Suggested Resolution |
|---|-----------|---------|---------------------|
| 1 | **Tag source tracking**: Architecture says `source` column on Tag distinguishes scraper vs. user tags. Existing code has no `source` column on Tag entity. | Architecture §Data Model vs existing `Tag.cs` | **Add `Source` column** to Tag (nullable string: "scraper" or "user"). Or track via metadata.json union merge without a DB column. Discuss with owner. |
| 2 | **PrintRecord as separate table vs JSONB**: Architecture ERD shows `PrintRecord` as a separate entity with FK to Model3D. Existing code stores `PrintHistory` as JSONB on Model3D. | Architecture §Data Model vs existing `Model3D.cs` | **Keep JSONB** for simplicity (no separate table needed for a single-user app). The JSONB approach is already implemented and works. |
| 3 | **metadata.json writeback**: Spec says Forgekeeper writes user-owned fields (print history, components, etc.) back to metadata.json for database-free recovery. No code implements this yet. | Spec §Writing metadata.json | **Add to WP3** — after any user edit that affects a metadata.json-owned field, re-serialize and write to disk. Respect the ownership matrix. |
| 4 | **Bulk operations API**: Spec mentions "tag multiple models at once, set category for entire creator." No endpoint defined. | Spec §Phase 2 | **Add `POST /api/v1/models/bulk`** accepting `{ modelIds: [...], operation: "tag\|categorize\|setGameSystem\|setScale", value: "..." }`. Add to WP5. |
| 5 | **Plugin vs Source Adapter**: Spec describes `ISourceAdapter` for reading directories. Library Scraper Plugin describes `ILibraryScraper` for downloading + organizing. Both concepts exist. Are they separate? | Spec §Source Adapters vs Library Scraper Plugin | **Yes, they are separate.** Source adapters are passive (read existing directories). Scraper plugins are active (download new files). A scraper plugin's output is then read by a source adapter during scanning. The MMF scraper plugin writes to `sources/mmf/`, and the `MmfSourceAdapter` reads from it. |
| 6 | **`sources` table vs SourceType enum**: The code has both a `Source` entity (database table) and a `SourceType` enum. Model3D has both `Source` (enum) and `SourceEntityId` (FK). Is the enum needed once the table exists? | Existing code | **Keep both for now.** The enum provides type safety and quick filtering. The Source entity provides configuration (base path, adapter type). Long-term, the enum may be removed in favor of the slug string. |
| 7 | **Thumbnail path: model-level vs variant-level**: Model3D has `ThumbnailPath`, Variant also has `ThumbnailPath`. Which is canonical? | Existing code | **Model-level** is the primary thumbnail (first/best variant's render). Variant-level is optional per-file thumbnails. The model thumbnail is shown in the browse grid. |

---

*This implementation plan is the canonical reference for building Forgekeeper. For design rationale and background context, refer to the source documents linked at the top. For the development environment setup, see the Dev Environment Guide.*
