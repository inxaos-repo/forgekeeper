# Consistency Review — Code vs Documentation

## Review Date: 2026-04-16
## Reviewed By: Bob the Skull (AI)

### Reference Documents
- **Architecture:** Wiki.js page 24 (`plans/forgekeeper/architecture`)
- **Implementation Plan:** Wiki.js page 25 (`plans/forgekeeper/implementation-plan`)
- **Spec:** Wiki.js page 20 (`plans/forgekeeper/spec`)
- **Codebase:** `/home/openclaw/.openclaw/workspace/projects/forgekeeper/` (main branch, fresh merge)

---

### Critical Issues (must fix before release)

#### C1. ILibraryScraper Interface Signature Mismatch (Code vs Architecture Doc)

**Architecture doc (page 24)** defines `ILibraryScraper` with these signatures:
```csharp
Task<IReadOnlyList<LibraryItem>> GetLibraryAsync(CancellationToken ct);
Task<ScrapeResult> ScrapeModelAsync(LibraryItem item, string targetDirectory, CancellationToken ct);
Task<bool> HasUpdateAsync(LibraryItem item, DateTime lastDownloaded);
IReadOnlyList<ConfigField> ConfigFields { get; }
Task InitializeAsync(IReadOnlyDictionary<string, string> config);
```

**Implementation plan (page 25, WP8)** defines it as:
```csharp
Task<IReadOnlyList<ScrapedModel>> FetchManifestAsync(PluginContext context, CancellationToken ct);
Task<ScrapeResult> ScrapeModelAsync(ScrapedModel model, PluginContext context, CancellationToken ct);
Task<bool> HandleAuthCallbackAsync(HttpContext httpContext, PluginContext context);
IReadOnlyList<PluginConfigField> ConfigSchema { get; }
```

**Code (`PluginSdk/ILibraryScraper.cs`)** uses:
```csharp
Task<IReadOnlyList<ScrapedModel>> FetchManifestAsync(PluginContext context, Stream? uploadedManifest = null, CancellationToken ct = default);
Task<ScrapeResult> ScrapeModelAsync(PluginContext context, ScrapedModel model, CancellationToken ct = default);
Task<AuthResult> HandleAuthCallbackAsync(PluginContext context, IDictionary<string, string> callbackParams, CancellationToken ct = default);
```

**Three different signatures across three documents.** Key differences:
- Architecture uses `LibraryItem` (doesn't exist in code), code uses `ScrapedModel` ✅
- Architecture uses `ConfigField` (doesn't exist), code uses `PluginConfigField` ✅
- Architecture has `GetLibraryAsync` / `InitializeAsync` / `HasUpdateAsync`, code has `FetchManifestAsync` / no explicit Init / no HasUpdateAsync
- Impl plan has `HandleAuthCallbackAsync(HttpContext, PluginContext)`, code has `HandleAuthCallbackAsync(PluginContext, IDictionary<string, string>, CancellationToken)`
- Impl plan has `ScrapeModelAsync(ScrapedModel, PluginContext, CancellationToken)`, code has params reversed: `ScrapeModelAsync(PluginContext, ScrapedModel, CancellationToken)`
- Code adds `Stream? uploadedManifest` parameter to `FetchManifestAsync` (not in any doc)

**Impact:** A plugin developer reading the Architecture doc would write incompatible code. The docs need to converge on the actual code signatures.

#### C2. Architecture Doc References `Forgekeeper.Frontend`, Code Uses `Forgekeeper.Web`

Architecture doc, Dockerfile examples, and directory structure sections all reference:
- `src/Forgekeeper.Frontend/` — the Vue.js SPA project
- `Forgekeeper.Frontend/src/views/`, `Forgekeeper.Frontend/src/components/`, etc.

Actual codebase:
- `src/Forgekeeper.Web/` (with `package.json`, `vite.config.js`)

The impl plan also references `Forgekeeper.Web` in the directory structure, so the plan is correct but the architecture doc is wrong.

**Impact:** Dev setup instructions in architecture doc point to wrong directory.

#### C3. Missing `Forgekeeper.PluginHost` Project

**Implementation plan** specifies a dedicated project:
- `src/Forgekeeper.PluginHost/` — plugin loading, lifecycle, scheduling

**Code:** No `Forgekeeper.PluginHost` project exists. Plugin hosting is integrated into `Forgekeeper.Infrastructure/Services/PluginHostService.cs`.

The dependency rules in the impl plan say:
> `Forgekeeper.PluginHost` references Core, PluginSdk

But in code, `PluginHostService` lives in Infrastructure and depends on Infrastructure's `ForgeDbContext` directly.

**Impact:** The project structure differs from documented architecture. Not blocking, but a developer expecting to find a separate PluginHost project will be confused.

#### C4. PrintRecord Entity Missing — PrintHistory is JSONB Instead

**Implementation plan WP2** specifies:
> Task 2.8: Implement `PrintRecord` entity (new — from architecture doc) — model_id FK, print_date, printer, technology, material, layer_height, scale_factor, result, notes

**Architecture doc** Data Model shows:
```
PrintRecord {
    uuid id PK
    uuid model_id FK
    date print_date
    string printer
    ...
}
```

**Code:** No `PrintRecord` entity exists. Print history is stored as a `List<PrintHistoryEntry>` JSONB column on `Model3D`. `ForgeDbContext` has no `DbSet<PrintRecord>`, no `print_records` table.

**Impact:** This is a design decision that differs from docs. The JSONB approach has tradeoffs (harder to query, but simpler schema). The docs should be updated to reflect the actual implementation choice — otherwise the impl plan task 2.8 reads as incomplete work when it's actually a deliberate design change.

#### C5. Bulk Update API Contract Mismatch

**Implementation plan §4** defines the bulk update endpoint as:
```json
POST /api/v1/models/bulk
{
  "modelIds": ["uuid1", "uuid2"],
  "updates": {
    "addTags": ["tag1"],
    "removeTags": ["old-tag"],
    "category": "tabletop",
    ...
  }
}
```

**Code (`ModelEndpoints.cs`)** implements:
```json
POST /api/v1/models/bulk
{
  "modelIds": ["uuid1", "uuid2"],
  "operation": "tag",
  "value": "tag-name"
}
```

The code uses a single `operation` + `value` pattern (one operation at a time), not the multi-field `updates` object from the docs. The code also supports operations: `tag`, `categorize`, `setgamesystem`, `setscale`, `setrating`, `setlicense`.

**Impact:** API contract differs — anyone building a client from the docs will send the wrong request body.

---

### Moderate Issues (should fix)

#### M1. Search Uses ILIKE Instead of pg_trgm similarity()

**All three docs** specify fuzzy search via `pg_trgm similarity()`:
```sql
WHERE similarity(m.name, {query}) > 0.3
```

**Code (`SearchService.cs`)** actually uses `EF.Functions.ILike()`:
```csharp
query = query.Where(m =>
    EF.Functions.ILike(m.Name, $"%{searchTerm}%") ||
    EF.Functions.ILike(m.Creator.Name, $"%{searchTerm}%") || ...);
```

ILIKE does substring matching, not fuzzy/trigram matching. Searching "drago" would NOT match "Dragon Knight" with ILIKE unless the substring appears. The GIN pg_trgm indexes exist in the migration but aren't used.

**Impact:** Search quality will be worse than documented. The `_minSimilarity` config value is loaded but never used.

#### M2. ThingiverseSourceAdapter Missing

**Spec** lists:
> | thingiverse | `ThingiverseSourceAdapter` | Yes | Thing ID in folder names |

**Code:** No `ThingiverseSourceAdapter` exists. Thingiverse is handled by `GenericSourceAdapter(SourceType.Thingiverse, "thingiverse")` in `Program.cs`.

**Impact:** The spec describes Thing ID parsing from folder names — `GenericSourceAdapter` doesn't do this.

#### M3. MCP Endpoint Structure Differs from Architecture Doc

**Architecture doc** specifies:
- SSE transport at `/mcp` (Server-Sent Events for streaming)
- Standalone stdio transport option (`forgekeeper-mcp` binary)
- Tool names prefixed with `forgekeeper_` (e.g., `forgekeeper_search`)

**Code:**
- Simple REST endpoints: `GET /mcp/tools` and `POST /mcp/invoke`
- No SSE transport
- No stdio transport
- Tool names are un-prefixed: `search`, `getModel`, `tagModel`, etc.

**Impact:** MCP clients expecting standard SSE/stdio transports won't connect. Tool names don't follow the documented convention.

#### M4. MCP `armyCheck` Tool Missing

**Implementation plan WP14** lists:
> `forgekeeper_army_check` — Search + fuzzy match against army list text

**Code (`ForgekeeperMcpServer.cs`):** No `armyCheck` tool implemented. The tool definitions include `collectionReport`, `healthCheck`, and `printHistory` but not `armyCheck`.

**Impact:** Documented MCP capability is missing.

#### M5. Response Envelope Format Differs

**Architecture doc** specifies response format:
```json
{
  "data": [...],
  "pagination": { "page": 1, "pageSize": 50, "totalItems": 7223, "totalPages": 145 }
}
```

**Code** uses `PaginatedResult<T>`:
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

Key differences:
- `data` → `items`
- `pagination.totalItems` → flat `totalCount`
- Flat structure vs nested `pagination` object
- Code adds `hasNext`/`hasPrevious` (not in docs)

**Impact:** Frontend or API clients built from the architecture doc will expect the wrong response shape.

#### M6. `GET /api/v1/creators/{id}/models` Returns Flat List, Not Paginated

**Implementation plan** specifies:
```
GET /api/v1/creators/{id}/models → PaginatedResult<ModelResponse>
```

**Code (`CreatorEndpoints.cs`):** Returns `List<ModelResponse>` directly without pagination:
```csharp
var models = await modelRepo.GetByCreatorIdAsync(id, ct);
var response = models.Select(m => new ModelResponse { ... }).ToList();
return Results.Ok(response);
```

**Impact:** A creator with 1000+ models would return all of them in one response.

#### M7. Source Seed Data Not Implemented

**Implementation plan §3** notes:
> Seeding: Initial sources (mmf, thangs, patreon, cults3d, thingiverse, manual) seeded in migration or on first startup

**Code:** No source seeding in migrations or startup. The `Sources` table starts empty. Users must manually create sources via `POST /api/v1/sources`.

**Impact:** Scanner won't work out of the box until sources are configured.

#### M8. `PluginHostService` References Non-Existent `Forgekeeper:PluginsDirectory` Config

**Code (`PluginHostService.cs`):**
```csharp
_pluginsDirectory = configuration["Forgekeeper:PluginsDirectory"] ?? "/app/plugins";
_sourcesDirectory = configuration["Forgekeeper:SourcesDirectory"] ?? "/mnt/3dprinting/sources";
```

**`appsettings.json`:** These config keys don't exist. The defaults will be used, but `docker-compose.yml` does set `Forgekeeper__PluginsDirectory` and `Forgekeeper__SourcesDirectory` as env vars.

**Impact:** Config is split between two sources and not documented in appsettings.json, which is confusing for developers.

#### M9. Architecture Doc's `Forgekeeper.PluginHost.Tests` Doesn't Exist

**Implementation plan** lists in repository structure:
```
tests/
├── Forgekeeper.Tests/
└── Forgekeeper.PluginHost.Tests/
```

**Code:** Only `tests/Forgekeeper.Tests/` exists. Plugin host tests (`PluginHostServiceTests.cs`) are in the main test project.

#### M10. `estimatedResin` Field Mismatch in PrintSettings

**Architecture doc metadata.json schema:**
```json
"printSettings": { "estimatedResin": "45ml" }
```

**Code (`PrintSettingsInfo`):**
```csharp
public string? EstimatedMaterial { get; set; }  // "estimatedMaterial"
```

The JSON property name is `estimatedMaterial` in code but `estimatedResin` in the architecture doc. The code is more generic (material vs resin-specific).

#### M11. `ScannerWorker` BackgroundService Missing

**Implementation plan** references:
```
BackgroundServices/ — ScannerWorker, ThumbnailWorker
```

**Code:** Only `ThumbnailWorker.cs` exists in `BackgroundServices/`. There's no `ScannerWorker` — scanning is on-demand via API (`POST /api/v1/scan`) or triggered by the `PluginHostService` after plugin sync. No periodic background scanning.

**Impact:** Docs describe automatic periodic scanning that doesn't exist.

#### M12. `init-db.sql` Missing

**Implementation plan WP1:**
> Task 1.3: Create `init-db.sql` with `CREATE EXTENSION IF NOT EXISTS pg_trgm;`

**Code:** No `init-db.sql` file. The `pg_trgm` extension is created via EF Core's `modelBuilder.HasPostgresExtension("pg_trgm")` in `ForgeDbContext`, which handles it during migration.

**Impact:** Minor — the EF approach is actually better. Just a doc mismatch.

---

### Minor Issues (nice to fix)

#### m1. Local IMPLEMENTATION-PLAN.md Has Sanitized URLs

The `IMPLEMENTATION-PLAN.md` in the repo has sanitized links:
```
https://wiki.k8s.example.com/plans/forgekeeper/spec
```

While the Wiki.js version has the real URLs:
```
https://wiki.k8s.inxaos.com/plans/forgekeeper/spec
```

The local copy also doesn't include the MiniDownloader link that the Wiki version has.

#### m2. Architecture Doc References `Forgekeeper.Frontend` in Dockerfile

The Dockerfile example in the architecture doc uses:
```dockerfile
COPY src/Forgekeeper.Frontend/package*.json .
COPY src/Forgekeeper.Frontend .
```

But the actual project directory is `src/Forgekeeper.Web/`.

The **implementation plan's** Dockerfile also references `Forgekeeper.Web` in the directory structure but has a different Dockerfile layout that includes PluginHost and PluginSdk in the restore step.

#### m3. `Forgekeeper.PluginSdk` Doesn't Reference `Core`

**Implementation plan** says:
> `Forgekeeper.PluginSdk` may reference Core

**Code:** PluginSdk is completely standalone — no reference to Core. Types like `PluginConfigField`, `ScrapedModel`, etc. are defined directly in the SDK. This is actually the correct pattern for a NuGet package, but differs from the dependency diagram.

#### m4. `manifest.json` vs `plugin manifest`

**Architecture doc** describes plugin loading expecting `manifest.json`:
```json
{
  "id": "forgekeeper-scraper-mmf",
  "name": "MyMiniFactory Scraper",
  "entryAssembly": "Forgekeeper.Scraper.Mmf.dll",
  "entryType": "Forgekeeper.Scraper.Mmf.MmfScraper"
}
```

**Code (`PluginHostService.cs`):** Doesn't read `manifest.json` at all. Plugin discovery scans DLLs in plugin directories and uses reflection to find `ILibraryScraper` implementations. Plugin metadata comes from the interface properties (`SourceSlug`, `SourceName`, `Version`), not a manifest file.

#### m5. `CreatorDetailResponse` Doesn't Match Architecture Doc

**Architecture doc** API contract: `GET /api/v1/creators/{id}` returns `CreatorDetailResponse (includes stats + models)`

**Code:** `CreatorDetailResponse` includes `TotalSizeBytes`, `TotalFileCount`, and inline `Models` list, but the models are loaded separately via `modelRepo.GetByCreatorIdAsync()` rather than from `creator.Models` navigation.

This works but loads all models for the creator in one call — no pagination.

#### m6. Spec References `MMFDownloader` as "Damon's Custom App"

**Spec (page 20):**
> MMFDownloader is Damon's custom C# (.NET 8) tool for backing up his MyMiniFactory library

The spec contains multiple "Damon" references in the MiniDownloader section. These are informational (context about the tool's origin) rather than code references, but they tie the product to a specific person.

#### m7. Docker Compose Port Discrepancy

**Architecture doc Kubernetes Ingress** uses port 5000 for the backend.
**`docker-compose.yml`** maps `5000:5000`.
**`docker-compose.dev.yml`** also maps `5000:5000`.

All consistent. However, `appsettings.json` uses `Host=localhost;Port=5432` (standard PG port) while `docker-compose.dev.yml` maps `5433:5432`. The dev container connects via hostname `db:5432` (internal), which is correct, but anyone connecting from the host would need port 5433.

#### m8. `Forgekeeper.Core/Interfaces/ILibraryScraper` Missing

Architecture doc says interfaces live in Core:
> `Interfaces/ — IModelRepository, IScannerService, ILibraryScraper`

**Code:** `ILibraryScraper` is in `Forgekeeper.PluginSdk`, not Core. Core has `ISourceAdapter` (different interface for directory-level source adapters). This is correct for the plugin design, but the architecture doc's list is misleading.

#### m9. MCP Tool Names Don't Match Implementation Plan

**Implementation plan WP14** lists tools as `forgekeeper_search`, `forgekeeper_get_model`, etc.

**Code:** Tools are named `search`, `getModel`, `getCreator`, etc. (no prefix, camelCase).

#### m10. `PluginSdk` Has `MetadataFile` Type Referenced in Docs but Not in Code

**Implementation plan WP8 task 8.2:**
> Implement supporting types in SDK: `PluginContext`, `ScrapedModel`, `ScrapeResult`, `AuthResult`, `PluginConfigField`, `ScrapeProgress`, `DownloadedFile`, **`MetadataFile`**

**Code:** No `MetadataFile` type in PluginSdk. The `SourceMetadata` type exists in Core.

#### m11. Architecture Doc Plugin `entryType` References Wrong Class Name

**Architecture doc:**
```json
{ "entryType": "Forgekeeper.Scraper.Mmf.MmfScraper" }
```

**Code:** The actual class is `Forgekeeper.Scraper.Mmf.MmfScraperPlugin`.

---

### Personal References Check

Searched all `.cs`, `.json`, `.md`, `.yaml`, `.yml` files (excluding `obj/` and `bin/`) for personal identifiers.

| Pattern | Files Found | Assessment |
|---------|-------------|------------|
| `Damon` | `SPEC.md` (MiniDownloader section) | Informational context about the tool's origin. **Keep as-is** — this is the spec, not shipping code. |
| `Bob` | None in code | Clean |
| `ginaz` | None in code | Clean |
| `inxaos` | `IMPLEMENTATION-PLAN.md` (Wiki.js links in wiki version only — local copy has `example.com`) | Wiki.js version has real URLs with `inxaos.com`. Local copy is sanitized. |
| `Arrakis` | None in code | Clean |
| `myminifactory.com` | `plugins/Forgekeeper.Scraper.Mmf/MmfScraperPlugin.cs`, `SPEC.md`, `IMPLEMENTATION-PLAN.md`, `tests/MetadataParsingTests.cs` | **Expected** — MMF scraper legitimately needs MMF URLs. Test data uses example MMF URLs. |
| `downloader_v2` / `6b511607` | `IMPLEMENTATION-PLAN.md` only | **MMF API credentials in docs** — these are the MiniDownloader OAuth credentials. Not in code, only in the implementation plan doc. |
| `Damon Prater` | `IMPLEMENTATION-PLAN.md` (impl plan WP9 manifest.json example: `"author": "Damon Prater"`) | In the wiki version's manifest example. Should be genericized. |
| `forge.k8s.inxaos.com` | Architecture doc (k8s Ingress), impl plan (deployment) | Only in docs/deployment examples, not in code. Code uses config-driven hostnames. |

**Summary:** Code is clean of personal references. Docs contain expected references (spec context, deployment examples). The only concern is the MMF OAuth client ID/secret being documented in the implementation plan — those should be treated as secrets, not hardcoded in docs.

---

### Test Coverage Check

| WP | Claimed Tests | Actual Test File | Status |
|----|---------------|-----------------|--------|
| WP3: File Scanner | Scanner integration tests | `FileScannerServiceTests.cs` | ✅ Exists |
| WP3: Source Adapters | Adapter parsing tests | `SourceAdapterTests.cs` | ✅ Exists |
| WP3: Variant Detection | Variant detection rules | `VariantDetectionTests.cs` | ✅ Exists |
| WP3: Metadata Parsing | metadata.json parsing | `MetadataParsingTests.cs`, `MetadataServiceTests.cs` | ✅ Exists |
| WP4: Search Service | Search integration tests | `SearchServiceTests.cs` | ✅ Exists |
| WP5: API Endpoints | API integration tests | `ApiIntegrationTests.cs` | ✅ Exists |
| WP6: Import Service | Import pipeline tests | `ImportServiceTests.cs` | ✅ Exists |
| WP8: Plugin Host | Plugin host tests | `PluginHostServiceTests.cs` | ✅ Exists |
| WP9: MMF Plugin | MMF scraper tests | `MmfScraperPluginTests.cs` | ✅ Exists |
| WP5: Repository | CRUD tests | `RepositoryTests.cs` | ✅ Exists |
| WP15: Import Confidence | Confidence scoring | (not a separate file) | ⚠️ May be in `ImportServiceTests.cs` |
| WP15: E2E Tests | Frontend E2E | None | ❌ Missing (marked as optional in plan) |

---

### Summary

| Severity | Count | Key Themes |
|----------|-------|------------|
| **Critical** | 5 | Plugin interface drift (3 versions), project naming (`Frontend` vs `Web`), missing `PluginHost` project, `PrintRecord` vs JSONB, bulk update API contract |
| **Moderate** | 12 | Search not using pg_trgm, missing source adapter, MCP transport/naming, response envelope format, no pagination on creator models, missing seed data, missing periodic scanner |
| **Minor** | 11 | Sanitized URLs, Dockerfile paths, manifest.json not used, class name typos, doc inconsistencies |

**Total issues: 5 critical, 12 moderate, 11 minor**

### Recommendation

The code is well-structured and comprehensive — it implements the vast majority of what the docs describe. The main problem is **documentation drift**: the Architecture doc represents an earlier design that was refined during implementation. A single pass to update the Architecture doc (page 24) to match the actual code would resolve most critical and moderate issues.

Priority fixes:
1. **Update Architecture doc's `ILibraryScraper` definition** to match actual `PluginSdk/ILibraryScraper.cs`
2. **Update Architecture doc's response format** to match `PaginatedResult<T>`
3. **Update bulk update API contract** in implementation plan to match actual `BulkUpdateRequest`
4. **Rename `Forgekeeper.Frontend` → `Forgekeeper.Web`** in Architecture doc
5. **Add note about `PrintRecord` → JSONB decision** in implementation plan
6. **Switch `SearchService` to use actual `similarity()` function** instead of ILIKE to match documented behavior and use the pg_trgm indexes that were created
