# Forgekeeper — 3D Print File Manager

> **Status:** Spec Draft
> **Created:** 2026-04-15
> **Stack:** C# (ASP.NET Core 9) + PostgreSQL + Vue.js + Three.js
> **Target:** Self-hosted k8s deployment, designed for massive collections (300K+ files)

---

## Problem Statement

Managing a 3.9TB / 346K file 3D printing collection with existing tools (Manyfold) is inadequate:
- **Variant handling is broken** — supported/unsupported versions of the same model are treated as separate models instead of variants
- **Scale issues** — Manyfold chokes on 346K files, gets progressively slower
- **No creator intelligence** — 72% of files come from MMFDownloader with a known creator structure that should be leveraged
- **Search is weak** — need full-text search across filenames, creator names, tags, and categories

## Storage Architecture

### Design Principle: Source-Parallel Directories with Unified UX

Forgekeeper maintains **separate directory structures per source** on disk, each following a canonical layout. The database provides a **unified view** across all sources. This keeps source-specific tooling (MMFDownloader, future scrapers) working independently while presenting one seamless experience to the user.

### Canonical Directory Layout

```
/mnt/3dprinting/
├── sources/
│   ├── mmf/                        # MyMiniFactory (via MMFDownloader)
│   │   └── CreatorName/
│   │       └── ModelName - ModelID/
│   │           ├── supported/
│   │           ├── unsupported/
│   │           ├── presupported/
│   │           ├── lychee/
│   │           ├── images/
│   │           └── metadata.json
│   ├── thangs/                     # Thangs downloads
│   │   └── CreatorName/
│   │       └── ModelName/
│   ├── patreon/                    # Patreon creator drops
│   │   └── CreatorName/
│   │       └── YYYY-MM Release Name/
│   ├── cults3d/                    # Cults3D downloads
│   │   └── CreatorName/
│   │       └── ModelName/
│   ├── thingiverse/                # Thingiverse downloads
│   │   └── CreatorName/
│   │       └── ModelName/
│   └── manual/                     # Manually organized files
│       └── CreatorName/
│           └── ModelName/
├── unsorted/                       # DROP ZONE for auto-sort
│   └── (anything: ZIPs, loose STLs, folders, chaos)
└── .forgekeeper/
    └── thumbnails/
```

### Source Adapters

Each source has an adapter (implementing `ISourceAdapter`) that handles scanning and import:

| Source | Adapter | Auto-Scan | Notes |
|--------|---------|-----------|-------|
| **mmf** | `MmfSourceAdapter` | Yes | MMFDownloader output = canonical schema (a custom downloader app) |
| **thangs** | `GenericSourceAdapter` | Yes | Creator/Model structure |
| **patreon** | `PatreonSourceAdapter` | Yes | Date-based releases (YYYY-MM/) |
| **cults3d** | `GenericSourceAdapter` | Yes | Simpler structure |
| **thingiverse** | `ThingiverseSourceAdapter` | Yes | Thing ID in folder names |
| **manual** | `GenericSourceAdapter` | Yes | User-organized |

### Import Flow (Unsorted -> Sorted)

When files land in `/unsorted/`:

1. **Auto-detect** what we can:
   - Extract ZIPs/RARs automatically
   - Parse filenames for creator hints, model names, IDs
   - Check for metadata files (README, metadata.json)
   - Match against known creators in the database
   - Detect variant patterns (supported/unsupported subfolders)

2. **Queue for user review** what we can't:
   - Unknown creator -> "Who made this?"
   - Ambiguous model name -> "What is this?"
   - Unknown source -> "Where did this come from?" (MMF, Thangs, Patreon, etc.)
   - Unclear variant type -> "Supported or unsupported?"

3. **Move to canonical location** once confirmed:
   - Files move from `unsorted/` to `sources/{source}/{creator}/{model}/{variant}/`
   - Database entry created
   - Original ZIP archived or deleted (user preference)

4. **Learn from decisions** -- remember creator-to-source mappings for future imports

### MMFDownloader Integration

MMFDownloader (a custom downloader app) output IS the canonical format. The `MmfSourceAdapter` is the reference implementation. Other adapters map their structure INTO this same pattern on disk. Future: add `metadata.json` output to MMFDownloader with source URL, download date, model ID.

### Unified UX

The frontend shows ALL models from ALL sources in one view. Users can:
- Browse everything together (default)
- Filter by source (show only MMF, only Patreon, etc.)
- Filter by creator (across sources -- same creator on MMF + Patreon merged)
- The source badge on each model shows where it came from
- Clicking through shows the actual filesystem path for manual access

### Key Facts
- File types: `.stl`, `.obj`, `.3mf`, `.lys`, `.chitubox`, `.gcode`
- Some models have 50+ variants (poses, scales, parts)
- Collection is overwhelmingly tabletop wargaming miniatures
- 72% of current collection from MMFDownloader (165 creators)
- Total: 3.9TB, 346K files, 55K directories




---

## Library Lifecycle Workflow

```kroki
mermaid

flowchart TB
    subgraph ACQUIRE["1. ACQUIRE"]
        direction TB
        MMF[("MyMiniFactory\n(the MMF scraper plugin)")]
        THANGS[("Thangs\n(manual download)")]
        PATREON[("Patreon\n(monthly drops)")]
        CULTS[("Cults3D\n(manual download)")]
        THINGI[("Thingiverse\n(manual download)")]
        MANUAL[("Direct STL\n(shared links, etc)")]
    end

    subgraph INGEST["2. INGEST"]
        direction TB
        MMF_DIR["sources/mmf/\n(auto-sorted by\nthe MMF scraper plugin)"]
        UNSORTED["unsorted/\nDROP ZONE"]
        AUTO["Auto-Detect\n- Extract ZIPs\n- Parse filenames\n- Match creators\n- Detect variants"]
        REVIEW["User Review Queue\n- Confirm creator\n- Confirm source\n- Set model name\n- Classify variants"]
        SORTED["Move to\nsources/{source}/\n{creator}/{model}/"]
    end

    subgraph ORGANIZE["3. ORGANIZE"]
        direction TB
        DB[("PostgreSQL\nForgekeeper DB")]
        SCAN["Scanner Service\n- Walk source dirs\n- Index to DB\n- Detect changes"]
        TAG["Tag & Classify\n- Game system\n- Scale\n- Category\n- Custom tags"]
        THUMB["Thumbnail Generator\n- STL render\n- Store .webp"]
    end

    subgraph USE["4. USE"]
        direction TB
        SEARCH["Search & Browse\n(Web UI)"]
        PREVIEW["3D Preview\n(Three.js)"]
        DOWNLOAD["Download / Copy\nto slicer"]
        PRINT["Mark as Printed\n+ Notes/Rating"]
    end

    subgraph MAINTAIN["5. MAINTAIN"]
        direction TB
        RESCAN["Periodic Rescan\n(detect new/changed)"]
        DEDUP["Duplicate Detection\n(cross-source)"]
        BACKUP["NFS Backup\n(NFS storage)"]
        UPDATE["Re-download\nupdated models"]
    end

    MMF -->|"auto-sorted"| MMF_DIR
    THANGS -->|"dump"| UNSORTED
    PATREON -->|"dump"| UNSORTED
    CULTS -->|"dump"| UNSORTED
    THINGI -->|"dump"| UNSORTED
    MANUAL -->|"dump"| UNSORTED

    MMF_DIR --> SCAN
    UNSORTED --> AUTO
    AUTO -->|"confident"| SORTED
    AUTO -->|"uncertain"| REVIEW
    REVIEW -->|"confirmed"| SORTED
    SORTED --> SCAN

    SCAN --> DB
    DB --> TAG
    DB --> THUMB
    DB --> SEARCH

    SEARCH --> PREVIEW
    SEARCH --> DOWNLOAD
    DOWNLOAD --> PRINT

    DB --> RESCAN
    DB --> DEDUP
    DB --> BACKUP
    MMF -->|"re-check"| UPDATE
    UPDATE --> MMF_DIR

    style ACQUIRE fill:#1a1a2e,stroke:#4ecca3,color:#e0e0e0
    style INGEST fill:#16213e,stroke:#e94560,color:#e0e0e0
    style ORGANIZE fill:#0f3460,stroke:#e94560,color:#e0e0e0
    style USE fill:#533483,stroke:#4ecca3,color:#e0e0e0
    style MAINTAIN fill:#1a1a2e,stroke:#e94560,color:#e0e0e0
```

### Lifecycle Stages Explained

**1. ACQUIRE** — Files enter the system from multiple sources. MMF is automated via the MMF scraper plugin (the MMF scraper plugin). Everything else is manual downloads dumped into the unsorted zone.

**2. INGEST** — MMF files are already sorted (the MMF plugin creates Creator/Model structure). Other sources land in `unsorted/`, where Forgekeeper auto-detects what it can (ZIP extraction, filename parsing, creator matching) and queues the rest for user review. Confirmed files move to `sources/{source}/{creator}/{model}/`.

**3. ORGANIZE** — The scanner walks all source directories, indexes to PostgreSQL, and generates thumbnails. Users add tags, categories, game systems, and other metadata through the web UI.

**4. USE** — Browse, search, preview STLs in 3D, download to slicer, mark as printed with notes/ratings.

**5. MAINTAIN** — Periodic rescans detect new files. Duplicate detection across sources. NFS backups. the MMF scraper plugin can re-check for updated models.

---

## MMF Scraper Plugin

the MMF scraper plugin is The MMF scraper plugin handles downloading from MyMiniFactory. Key details:

- **Auth:** OAuth implicit flow to `auth.myminifactory.com` with local HTTP callback server
- **Library source:** Browser console dump of `/api/data-library/objectPreviews` (JSON), saved as manifest
- **Download flow:** Iterates manifest -> fetches `/api/v2/objects/{id}` for file URLs -> downloads individual files or archive ZIPs
- **Output structure:** `{RootPath}/{CreatorName}/{ModelName}/` with files flat inside (no supported/unsupported subfolders currently)
- **Dedup:** Skips existing files/directories
- **Rate limiting:** 1.5s delay between downloads
- **Token persistence:** Saved to `plugin config storage (database)`

### Integration Points for Forgekeeper
1. the MMF scraper plugin output maps to the `sources/mmf/` directory — Forgekeeper's `MmfSourceAdapter` reads it directly
2. The MMF scraper plugin should to write `metadata.json` per model (source URL, download date, file list, creator URL)
3. The `LibraryItem` model has `OriginalId`, `CreatorId`, `CreatorUsername` — all useful for Forgekeeper's database
4. The MMF scraper plugin can add variant subfolder detection (move STLs with "supported" in the name to `supported/` subfolder)




---

## Integration Contract: metadata.json

Forgekeeper defines a **source-agnostic** `metadata.json` schema. Any external tool (downloader, scraper, manual organizer) that writes this file into a source directory enables rich import without Forgekeeper needing to know anything about the tool itself.

**This is the contract between downloaders and Forgekeeper.** Forgekeeper never calls external APIs — it only reads directories and metadata files.

### metadata.json Schema (v1)

```json
{
  "metadataVersion": 1,
  
  "source": "mmf",
  "externalId": "123456",
  "externalUrl": "https://www.example.com/model/123456",
  
  "name": "Space Marine Captain with Power Sword",
  "description": "A highly detailed miniature...",
  "type": "object",
  "tags": ["warhammer", "40k", "space marine"],
  
  "creator": {
    "externalId": "789012",
    "username": "AwesomeSculptor",
    "displayName": "Awesome Sculptor Studio",
    "avatarUrl": "https://...",
    "profileUrl": "https://..."
  },
  
  "dates": {
    "created": "2025-06-15T10:30:00+00:00",
    "updated": "2025-08-20T14:00:00+00:00",
    "published": "2025-06-15T12:00:00+00:00",
    "addedToLibrary": "2025-07-01T00:00:00+00:00",
    "downloaded": "2026-04-15T18:00:00+00:00"
  },
  
  "acquisition": {
    "method": "purchase",
    "orderId": "F51015845C",
    "campaignId": null
  },
  
  "images": [
    {
      "url": "https://cdn.example.com/preview.jpg",
      "localPath": "images/preview_001.jpg",
      "type": "gallery"
    }
  ],
  
  "files": [
    {
      "filename": "captain_supported.stl",
      "localPath": "supported/captain_supported.stl",
      "size": 45678901,
      "variant": "supported",
      "downloadedAt": "2026-04-15T18:00:05+00:00"
    }
  ],
  
  "extra": {}
}
```

### Field Definitions

| Field | Required | Description |
|-------|----------|-------------|
| `metadataVersion` | Yes | Schema version (currently 1) |
| `source` | Yes | Source identifier slug (matches source directory name) |
| `externalId` | Yes | ID from the source platform (opaque string) |
| `externalUrl` | No | Link to the model on the source platform |
| `name` | Yes | Model display name |
| `description` | No | Model description (may contain HTML) |
| `type` | No | "object", "bundle", "collection" |
| `tags` | No | Array of tag strings from the source |
| `creator.externalId` | No | Creator's ID on the source platform |
| `creator.username` | No | URL-safe username |
| `creator.displayName` | No | Human-readable creator name |
| `creator.avatarUrl` | No | Avatar image URL |
| `creator.profileUrl` | No | Link to creator's profile |
| `dates.created` | No | When model was created on source |
| `dates.updated` | No | When model was last updated on source |
| `dates.published` | No | When model was published |
| `dates.addedToLibrary` | No | When user added it to their library |
| `dates.downloaded` | Yes | When the downloader tool grabbed the files |
| `acquisition.method` | No | How acquired: "purchase", "subscription", "free", "campaign", "gift" |
| `acquisition.orderId` | No | Order/transaction reference |
| `acquisition.campaignId` | No | Campaign/subscription ID if applicable |
| `images[]` | No | Preview images (URLs + local paths if downloaded) |
| `files[]` | No | Files downloaded with local paths and variant classification |
| `files[].variant` | No | Auto-detected: "supported", "unsupported", "presupported", "lychee", "other" |
| `extra` | No | Source-specific data that doesn't fit the standard schema. Opaque to Forgekeeper but preserved. |

### The `extra` Field

Source-specific data goes in `extra`. Forgekeeper stores it but doesn't interpret it. This is where private tools can stash platform-specific data without polluting the standard schema:

```json
"extra": {
  "mmf_bundle_id": 3147,
  "tribe_campaign": "monthly-2025-06",
  "license_type": "personal"
}
```

### How Forgekeeper Reads metadata.json

1. Scan source directory, find model folders
2. Check for `metadata.json` in each model folder
3. If present: **fast path** — import all standard fields, store `extra` as JSON blob
4. If absent: **fallback** — parse creator/model/variant from folder names and filenames (legacy support)
5. Forgekeeper writes `metadata.json` for manually imported items (from the unsorted/ flow) to enable database-free recovery. For scraper-provided metadata.json, Forgekeeper does NOT overwrite — its own metadata (tags, ratings, printed status, notes) lives in PostgreSQL only
6. Re-scanning a directory with existing `metadata.json` is idempotent — update DB if `dates.updated` changed

### Writing metadata.json (for downloader authors)

Any tool that writes files to a Forgekeeper source directory SHOULD also write `metadata.json`. At minimum:

```json
{
  "metadataVersion": 1,
  "source": "your-source-slug",
  "externalId": "unique-id-from-source",
  "name": "Model Name",
  "dates": { "downloaded": "2026-04-15T18:00:00Z" }
}
```

Everything else is optional but enriches the Forgekeeper experience. More data = better search, better organization, fewer manual tagging steps.


## Data Model

### Creator
```
id: UUID
name: string (unique)
source: enum (mmf, thingiverse, cults3d, manual)
source_url: string (nullable)
model_count: int (denormalized)
created_at: timestamp
```

### Model (parent object)
```
id: UUID
creator_id: FK → Creator
name: string
source_id: string (e.g., MMF model ID from folder name)
source_url: string (nullable)
category: string (nullable — tabletop, terrain, vehicle, prop, etc.)
tags: string[] (array)
scale: string (nullable — 28mm, 32mm, 75mm)
game_system: string (nullable — 40k, AoS, DnD, etc.)
file_count: int (denormalized)
total_size_bytes: bigint (denormalized)
thumbnail_path: string (nullable)
preview_images: string[] (paths to existing preview images)
base_path: string (filesystem path to model directory)
printed: boolean (default false)
rating: int (nullable, 1-5)
notes: text (nullable)
created_at: timestamp
updated_at: timestamp
```

### Variant
```
id: UUID
model_id: FK → Model
variant_type: enum (unsupported, supported, presupported, lychee, other)
file_path: string (relative to model base_path)
file_name: string
file_type: enum (stl, obj, 3mf, lys, chitubox, gcode, other)
file_size_bytes: bigint
thumbnail_path: string (nullable — generated STL thumbnail)
created_at: timestamp
```

### Tag
```
id: UUID
name: string (unique, lowercase)
```

### ModelTag (junction)
```
model_id: FK → Model
tag_id: FK → Tag
```

---

## Features

### Phase 1 — Core (MVP)

1. **File Scanner**
   - Walk the NFS directory tree recursively
   - Parse creator name, model name, model ID from folder structure
   - Auto-detect variant type from subfolder names
   - Handle MMFDownloader, Thingiverse, and manual folder structures
   - Detect existing preview images
   - Track file sizes and types
   - **Incremental scanning** — only process new/changed files on subsequent runs
   - Store scan state (last_modified timestamp per directory)

2. **Search & Browse**
   - Full-text search across model names, creator names, tags
   - Filter by: creator, category, game system, scale, file type, printed status
   - Sort by: name, date added, file count, total size, rating
   - Pagination (essential with 346K files)
   - PostgreSQL `pg_trgm` for fuzzy search

3. **Model Detail View**
   - Show all variants grouped by type (supported/unsupported/etc.)
   - Display existing preview images from the filesystem
   - Show file sizes, types, counts
   - Direct download links (serve files from NFS)
   - Edit tags, category, game system, scale, notes, rating, printed status

4. **Creator View**
   - List all models by a creator
   - Creator stats (model count, total size, file types)

5. **API**
   - RESTful API (FastAPI auto-generates OpenAPI docs)
   - All CRUD operations
   - Search endpoint with filters
   - Scan trigger endpoint
   - Stats/dashboard endpoint

### Phase 2 — Enhanced

6. **STL Thumbnail Generation**
   - Background worker generates thumbnails for STL/OBJ files
   - Use `stl-thumb` (Rust CLI) via `Process.Start()` or `SharpGLTF`/`Helix Toolkit` for native .NET rendering
   - Store thumbnails on NFS alongside the files
   - Generate on first scan, skip if thumbnail exists

7. **Bulk Operations**
   - Tag multiple models at once
   - Set category/game system for entire creator
   - Mark models as printed
   - Export selection as ZIP

8. **Import/Sync**
   - Watch for new files (inotify or periodic rescan)
   - Auto-import new creators/models from MMFDownloader drops
   - Detect deleted files and flag removed models

### Phase 3 — Advanced

9. **3D Preview**
   - Three.js in-browser STL viewer
   - Rotate, zoom, pan the model
   - Toggle between variants

10. **Slicer Integration**
    - "Send to Bambu Lab" or "Send to OctoPrint" buttons
    - Copy file to slicer watch folder

11. **Duplicate Detection**
    - Hash-based duplicate finding
    - Same model across different sources (MMF vs Thingiverse)

12. **Statistics Dashboard**
    - Collection size over time
    - Files by creator, category, scale
    - Printed vs unprinted ratio
    - Most common file types

---

## Technical Architecture

### Backend
- **C# / ASP.NET Core 9** (Minimal APIs)
- **Entity Framework Core 9** ORM with async support
- **PostgreSQL 16** via Npgsql (CNPG on Longhorn in k8s)
- **EF Core Migrations** for database schema management
- **Background workers** via `IHostedService` / `BackgroundService` for scanning + thumbnail generation
- **Built-in model validation** via data annotations + FluentValidation
- **.NET 9 AOT-ready** for fast container startup

### Frontend
- **Vue.js 3** (Composition API)
- **Three.js** for 3D STL preview
- **Tailwind CSS** or **PrimeVue** for UI components
- SPA with Vue Router
- Responsive (works on phone for browsing collection in the shop)

### Storage
- **NFS mount** to `/mnt/3dprinting/` (read-only for browsing, read-write for thumbnails)
- **PostgreSQL** for all metadata (CNPG on Longhorn)
- Thumbnails stored alongside models on NFS (`/mnt/3dprinting/.thumbnails/` or next to files)


### Project Structure
```
Forgekeeper/
├── Forgekeeper.Api/              # ASP.NET Core Minimal API project
│   ├── Program.cs                # App entry, service registration, middleware
│   ├── Endpoints/                # Minimal API endpoint groups
│   │   ├── ModelEndpoints.cs
│   │   ├── CreatorEndpoints.cs
│   │   ├── VariantEndpoints.cs
│   │   ├── TagEndpoints.cs
│   │   ├── ScanEndpoints.cs
│   │   └── StatsEndpoints.cs
│   └── appsettings.json
├── Forgekeeper.Core/             # Domain models, interfaces, DTOs
│   ├── Models/
│   │   ├── Creator.cs
│   │   ├── Model3D.cs
│   │   ├── Variant.cs
│   │   └── Tag.cs
│   ├── Interfaces/
│   │   ├── IModelRepository.cs
│   │   ├── IScannerService.cs
│   │   └── IThumbnailService.cs
│   └── DTOs/
├── Forgekeeper.Infrastructure/   # EF Core, NFS access, thumbnail generation
│   ├── Data/
│   │   ├── ForgeDbContext.cs
│   │   └── Migrations/
│   ├── Services/
│   │   ├── FileScannerService.cs
│   │   ├── ThumbnailService.cs
│   │   └── SearchService.cs
│   └── Repositories/
├── Forgekeeper.Web/              # Vue.js 3 SPA (served by ASP.NET or separate)
│   ├── src/
│   │   ├── views/
│   │   ├── components/
│   │   └── composables/
│   └── package.json
├── Forgekeeper.Tests/            # xUnit tests
├── Dockerfile
└── Forgekeeper.sln
```

### Deployment
- **Docker container** (multi-stage .NET 9 build, SDK → runtime-deps)
- **k8s Deployment** via Flux (HelmRelease or raw manifests)
- NFS PV mount for the 3D printing collection
- PostgreSQL via CNPG Cluster resource
- Ingress at `forge.example.com`

### Performance Targets
- Initial full scan of 346K files: < 30 minutes
- Incremental scan: < 2 minutes
- Search results: < 200ms for any query
- Page load with thumbnails: < 1 second
- Support 500K+ files without degradation

---

## Variant Detection Rules

Auto-detect variant type from folder names and file patterns:

| Folder/Pattern | Variant Type |
|---------------|-------------|
| `supported/`, `sup/` | supported |
| `unsupported/`, `unsup/`, `nosup/` | unsupported |
| `presupported/`, `pre-supported/`, `presup/` | presupported |
| `lychee/`, `*.lys` | lychee_project |
| `chitubox/`, `*.ctb`, `*.cbddlp` | chitubox_project |
| `*.gcode` | gcode |
| `*.3mf` | print_project |
| `images/`, `*.png`, `*.jpg` (non-STL) | preview_image |
| Everything else `.stl`, `.obj` | unsupported (default) |

### Model Grouping Rules
- All files within a single model directory (e.g., `CreatorName/ModelName - 12345/`) belong to ONE model
- Subfolders define variant types
- Files at the model root with no subfolder = unsupported variant (default)
- A model with ONLY supported variants and no unsupported → still one model, not separate

---

## Configuration

```json
// appsettings.json
{
  "ConnectionStrings": {
    "ForgeDb": "Host=forgekeeper-pg-rw;Port=5432;Database=forgekeeper;Username=forgekeeper;Password=password"
  },
  "Storage": {
    "BasePaths": ["/mnt/3dprinting"],
    "ThumbnailDir": ".thumbnails"
  },
  "Scanner": {
    "FileTypes": ["stl", "obj", "3mf", "lys", "ctb", "cbddlp", "gcode", "sl1"],
    "ImageTypes": ["png", "jpg", "jpeg", "webp", "gif"],
    "IgnorePatterns": ["*.tmp", "*.DS_Store", "Thumbs.db"],
    "Sources": [
      {
        "Name": "mmf",
        "Pattern": "*/MMFDownloader/*",
        "CreatorDepth": 1,
        "ModelDepth": 2
      },
      {
        "Name": "thingiverse",
        "Pattern": "*/Thingiverse/*",
        "CreatorDepth": 1,
        "ModelDepth": 2
      },
      {
        "Name": "manual",
        "Pattern": "*"
      }
    ]
  },
  "Search": {
    "MinTrigramSimilarity": 0.3,
    "ResultsPerPage": 50
  },
  "Thumbnails": {
    "Enabled": true,
    "Size": "256x256",
    "Format": "webp",
    "Renderer": "stl-thumb"
  }
}
```

---

## API Endpoints

```
GET    /api/v1/models                    # List/search models
GET    /api/v1/models/{id}               # Model detail + variants
PATCH  /api/v1/models/{id}               # Update model metadata
DELETE /api/v1/models/{id}               # Remove from DB (not filesystem)

GET    /api/v1/creators                  # List creators
GET    /api/v1/creators/{id}             # Creator detail + models
GET    /api/v1/creators/{id}/models      # Creator's models

GET    /api/v1/variants/{id}/download    # Download file (stream from NFS)
GET    /api/v1/variants/{id}/thumbnail   # Serve thumbnail

GET    /api/v1/tags                      # List all tags
POST   /api/v1/models/{id}/tags          # Add tags to model
DELETE /api/v1/models/{id}/tags/{tag}    # Remove tag

POST   /api/v1/scan                      # Trigger full scan
POST   /api/v1/scan/incremental          # Trigger incremental scan
GET    /api/v1/scan/status               # Scan progress

GET    /api/v1/stats                     # Collection statistics
GET    /api/v1/stats/creators            # Per-creator stats
```

---

## Name

**Forgekeeper** — because someone needs to keep the forge organized. Also sounds suitably Warhammer-adjacent.

---

## Open Questions

1. **Do you want to modify the existing folder structure**, or should Forgekeeper be read-only against the current layout?
2. **Are there other folder patterns** beyond MMFDownloader that I should account for? (Thingiverse downloads, manual saves, etc.)
3. **Priority: search vs. thumbnails?** Search is faster to build; thumbnails are more visual but take longer (generating 346K thumbnails = hours of CPU time)
4. **Game system tagging** — should we try to auto-detect game systems from creator/model names? (e.g., "Space Marine" → Warhammer 40K)
5. **Mobile use case** — will you browse this from your phone in the shop while standing at the printer?



---

## System Architecture

The following diagram shows the complete system architecture, from external sources through Forgekeeper's internal services to the end-user experience. The dashed boundary separates external acquisition tools (which Forgekeeper never controls) from Forgekeeper's own domain.

```kroki
mermaid

flowchart TB
    subgraph EXT["External Tools (NOT Forgekeeper)"]
        direction LR
        MMF_SRC["🌐 MyMiniFactory"]
        THANGS_SRC["🌐 Thangs"]
        PATREON_SRC["🌐 Patreon"]
        CULTS_SRC["🌐 Cults3D"]
        THINGI_SRC["🌐 Thingiverse"]

        MINI_DL["the MMF scraper plugin\n(.NET 8 Console)"]
        MANUAL_DL["Manual Downloads\n(Browser)"]
    end

    subgraph K8S["Kubernetes Cluster (k8s.example.com)"]
        direction TB

        subgraph FK["Forgekeeper Application"]
            direction TB

            subgraph BACKEND["ASP.NET Core 9 — Minimal APIs"]
                SCANNER["Scanner Service\n(BackgroundService)\n• Directory walking\n• Incremental detection\n• metadata.json parsing"]
                IMPORT["Import Service\n• ZIP extraction\n• Creator matching\n• Auto-sort / review queue"]
                SEARCH["Search Service\n• pg_trgm fuzzy search\n• Multi-field filtering\n• Pagination"]
                THUMB["Thumbnail Service\n(BackgroundService)\n• stl-thumb rendering\n• WebP output\n• Queue-based"]
            end

            subgraph FRONTEND["Vue.js 3 SPA"]
                UI["Web UI\n• Browse & Search\n• Model Details\n• Import Queue"]
                VIEWER["Three.js Viewer\n• STL/OBJ preview\n• Rotate/zoom/pan"]
            end
        end

        PG[("PostgreSQL 16\n(CNPG on Longhorn)\n• Metadata\n• Tags & ratings\n• Scan state")]
    end

    subgraph STORAGE["NFS Storage"]
        direction TB
        SRC_DIRS["sources/\n├── mmf/\n├── thangs/\n├── patreon/\n├── cults3d/\n├── thingiverse/\n└── manual/"]
        UNSORTED_DIR["unsorted/\n(drop zone)"]
        THUMB_DIR[".forgekeeper/thumbnails/"]
        META["metadata.json\n(per model dir)"]
    end

    INGRESS["forge.example.com\n(Ingress)"]
    USER["👤 User\n(Browser / Phone)"]

    MMF_SRC --> MINI_DL
    THANGS_SRC --> MANUAL_DL
    PATREON_SRC --> MANUAL_DL
    CULTS_SRC --> MANUAL_DL
    THINGI_SRC --> MANUAL_DL

    MINI_DL -->|"writes files +\nmetadata.json"| SRC_DIRS
    MANUAL_DL -->|"dumps files"| UNSORTED_DIR

    SCANNER -->|"reads"| SRC_DIRS
    SCANNER -->|"reads"| META
    SCANNER -->|"indexes"| PG
    IMPORT -->|"reads"| UNSORTED_DIR
    IMPORT -->|"moves to"| SRC_DIRS
    SEARCH -->|"queries"| PG
    THUMB -->|"reads STLs"| SRC_DIRS
    THUMB -->|"writes WebP"| THUMB_DIR

    UI --> SEARCH
    UI --> IMPORT
    VIEWER -->|"loads STL"| SRC_DIRS

    USER --> INGRESS --> UI

    style EXT fill:#2d1b1b,stroke:#e94560,color:#e0e0e0,stroke-dasharray: 5 5
    style K8S fill:#1a1a2e,stroke:#4ecca3,color:#e0e0e0
    style FK fill:#16213e,stroke:#4ecca3,color:#e0e0e0
    style STORAGE fill:#0f3460,stroke:#e94560,color:#e0e0e0
    style BACKEND fill:#1e3a5f,stroke:#4ecca3,color:#e0e0e0
    style FRONTEND fill:#533483,stroke:#4ecca3,color:#e0e0e0
```

---

## Component Architecture

The C# solution follows clean architecture principles with clear dependency directions. The Core project has zero external dependencies; Infrastructure implements Core interfaces; the API project wires everything together.

```kroki
mermaid

graph TB
    subgraph SLN["Forgekeeper.sln"]
        direction TB

        subgraph API["Forgekeeper.Api"]
            API_PROG["Program.cs\n• Service registration\n• Middleware pipeline\n• Auth configuration"]
            API_EP["Endpoints/\n• ModelEndpoints.cs\n• CreatorEndpoints.cs\n• ScanEndpoints.cs\n• StatsEndpoints.cs\n• TagEndpoints.cs"]
            API_BG["BackgroundServices/\n• ScannerWorker.cs\n• ThumbnailWorker.cs"]
        end

        subgraph CORE["Forgekeeper.Core"]
            CORE_MOD["Models/\n• Creator.cs\n• Model3D.cs\n• Variant.cs\n• Tag.cs\n• ScanState.cs"]
            CORE_INT["Interfaces/\n• IModelRepository.cs\n• ICreatorRepository.cs\n• IScannerService.cs\n• ISearchService.cs\n• IThumbnailService.cs\n• IImportService.cs\n• ISourceAdapter.cs"]
            CORE_DTO["DTOs/\n• ModelSearchRequest.cs\n• ModelResponse.cs\n• ImportQueueItem.cs\n• ScanProgress.cs"]
            CORE_ENUM["Enums/\n• VariantType.cs\n• SourceType.cs\n• AcquisitionMethod.cs"]
        end

        subgraph INFRA["Forgekeeper.Infrastructure"]
            INFRA_DATA["Data/\n• ForgeDbContext.cs\n• Migrations/\n• EntityConfigurations/"]
            INFRA_SVC["Services/\n• FileScannerService.cs\n• ThumbnailService.cs\n• SearchService.cs\n• ImportService.cs"]
            INFRA_REPO["Repositories/\n• ModelRepository.cs\n• CreatorRepository.cs"]
            INFRA_ADAPT["SourceAdapters/\n• MmfSourceAdapter.cs\n• PatreonSourceAdapter.cs\n• GenericSourceAdapter.cs"]
        end

        subgraph WEB["Forgekeeper.Web"]
            WEB_SRC["src/\n• views/ (Browse, ModelDetail,\n  CreatorDetail, ImportQueue)\n• components/ (ModelCard,\n  StlViewer, TagEditor)\n• composables/ (useSearch,\n  useModels, useImport)"]
            WEB_3D["Three.js Integration\n• STLLoader\n• OrbitControls\n• WebP thumbnails"]
        end

        subgraph TEST["Forgekeeper.Tests"]
            TEST_UNIT["Unit Tests\n• Scanner logic\n• Adapter parsing\n• Import rules"]
            TEST_INT["Integration Tests\n• DB queries\n• Search accuracy\n• File operations"]
        end
    end

    API -->|"depends on"| CORE
    API -->|"depends on"| INFRA
    INFRA -->|"implements"| CORE
    WEB -->|"HTTP calls"| API
    TEST -->|"tests"| CORE
    TEST -->|"tests"| INFRA

    style SLN fill:#1a1a2e,stroke:#4ecca3,color:#e0e0e0
    style API fill:#16213e,stroke:#e94560,color:#e0e0e0
    style CORE fill:#0f3460,stroke:#4ecca3,color:#e0e0e0
    style INFRA fill:#1e3a5f,stroke:#e94560,color:#e0e0e0
    style WEB fill:#533483,stroke:#4ecca3,color:#e0e0e0
    style TEST fill:#2d1b1b,stroke:#e94560,color:#e0e0e0
```

### Dependency Rules

| Project | References | External Packages |
|---------|-----------|-------------------|
| **Core** | _(none)_ | _(none — pure C#)_ |
| **Infrastructure** | Core | EF Core, Npgsql, FluentValidation |
| **Api** | Core, Infrastructure | ASP.NET Core, Serilog |
| **Web** | _(standalone SPA)_ | Vue 3, Three.js, Tailwind CSS |
| **Tests** | Core, Infrastructure | xUnit, Moq, Testcontainers |

---

## Import Pipeline

This sequence diagram shows the complete flow from a file landing in `unsorted/` through to it being searchable in the UI.

```kroki
mermaid

sequenceDiagram
    autonumber
    participant User as User / Downloader
    participant FS as NFS (unsorted/)
    participant Import as Import Service
    participant Sorted as NFS (sources/)
    participant Scanner as Scanner Service
    participant DB as PostgreSQL
    participant Thumb as Thumbnail Service
    participant UI as Web UI

    User->>FS: Drop files (ZIPs, folders, loose STLs)

    Note over Import: Periodic check or inotify trigger

    Import->>FS: Detect new files in unsorted/
    Import->>Import: Extract ZIPs/RARs
    Import->>Import: Parse filenames for hints
    Import->>DB: Query known creators for matching
    Import->>Import: Evaluate confidence score

    alt High Confidence (creator + source matched)
        Import->>Sorted: Move to sources/{source}/{creator}/{model}/
        Import->>DB: Create import log entry (auto-sorted)
    else Low Confidence (unknown creator/source)
        Import->>DB: Queue for user review
        Import-->>UI: Item appears in Import Queue
        UI->>User: Show review card (creator? source? name?)
        User->>UI: Confirm / correct details
        UI->>Import: Submit confirmed metadata
        Import->>Sorted: Move to sources/{source}/{creator}/{model}/
        Import->>DB: Create import log entry (user-confirmed)
    end

    Note over Scanner: Triggered after import or periodic schedule

    Scanner->>Sorted: Walk source directories
    Scanner->>Scanner: Read metadata.json (if present)
    Scanner->>Scanner: Parse folder structure (fallback)
    Scanner->>Scanner: Detect variant subfolders
    Scanner->>Scanner: Catalog file types and sizes
    Scanner->>DB: Upsert Creator record
    Scanner->>DB: Upsert Model3D record
    Scanner->>DB: Upsert Variant records
    Scanner->>DB: Update scan state timestamp

    Scanner->>Thumb: Queue models needing thumbnails
    Thumb->>Sorted: Read STL file
    Thumb->>Thumb: Render via stl-thumb
    Thumb->>FS: Write .webp to .forgekeeper/thumbnails/
    Thumb->>DB: Update thumbnail_path on Model3D

    UI->>DB: Search / browse query
    DB->>UI: Return results with thumbnail paths
    UI->>User: Display model cards with previews
```

---

## Data Flow

This diagram traces data from initial acquisition through to the user experience, highlighting what serves as the source of truth at each stage.

```kroki
mermaid

flowchart LR
    subgraph ACQUIRE["Acquisition Layer"]
        direction TB
        DL_TOOLS["Downloader Tools\n(the MMF scraper plugin, browser, etc.)"]
        DL_TOOLS -->|"writes"| FILES["Raw Files\n(.stl, .obj, .3mf, etc.)"]
        DL_TOOLS -->|"writes"| META_JSON["metadata.json\n(integration contract)"]
    end

    subgraph NFS_TRUTH["Source of Truth: FILES"]
        direction TB
        NFS["NFS Storage\n(your collection)"]
        NFS_FILES["Actual 3D model files\nOrganized in sources/"]
        NFS_THUMBS["Generated thumbnails\n.forgekeeper/thumbnails/\n(.webp cached renders)"]
    end

    subgraph FK_PROCESS["Forgekeeper Processing"]
        direction TB
        SCAN["Scanner\n• Reads files + metadata.json\n• Detects structure\n• Identifies variants"]
        IMPORT_SVC["Import Service\n• Auto-sort or queue\n• ZIP extraction\n• Creator matching"]
        THUMB_GEN["Thumbnail Generator\n• stl-thumb CLI\n• 256x256 WebP\n• Queue-based"]
        SEARCH_IDX["Search Service\n• pg_trgm indexing\n• Multi-field query\n• Fuzzy matching"]
    end

    subgraph PG_TRUTH["Source of Truth: METADATA"]
        direction TB
        PG[("PostgreSQL\n(CNPG / Longhorn)")]
        PG_DATA["User Metadata\n• Tags, ratings\n• Printed status\n• Notes, categories\n• Game system, scale"]
        PG_SCAN["Scan State\n• Last scan timestamps\n• File hashes\n• Import log"]
    end

    subgraph UX["User Experience"]
        direction TB
        BROWSE["Browse & Search\n(Vue.js SPA)"]
        DETAIL["Model Detail\n• Variants list\n• File downloads\n• Tag editor"]
        PREVIEW["3D Preview\n(Three.js)\n• STL rendered live\n• Rotate/zoom"]
    end

    FILES --> NFS_FILES
    META_JSON -->|"contract"| SCAN

    NFS_FILES --> SCAN
    SCAN --> PG
    SCAN --> THUMB_GEN
    IMPORT_SVC --> NFS_FILES
    THUMB_GEN --> NFS_THUMBS
    THUMB_GEN -->|"path ref"| PG

    PG --> SEARCH_IDX
    SEARCH_IDX --> BROWSE
    PG_DATA --> DETAIL
    NFS_FILES -->|"stream"| DETAIL
    NFS_FILES -->|"load"| PREVIEW
    NFS_THUMBS -->|"serve"| BROWSE

    style ACQUIRE fill:#2d1b1b,stroke:#e94560,color:#e0e0e0
    style NFS_TRUTH fill:#0f3460,stroke:#4ecca3,color:#e0e0e0
    style FK_PROCESS fill:#16213e,stroke:#e94560,color:#e0e0e0
    style PG_TRUTH fill:#1a1a2e,stroke:#4ecca3,color:#e0e0e0
    style UX fill:#533483,stroke:#4ecca3,color:#e0e0e0
```

### Source of Truth Boundaries

| Data Type | Source of Truth | Notes |
|-----------|----------------|-------|
| **3D model files** | NFS storage | Forgekeeper never copies or moves files outside NFS |
| **File structure** | NFS directory layout | Scanner reads this; it's authoritative |
| **External metadata** | `metadata.json` on NFS | Written by downloaders, read-only for Forgekeeper |
| **User metadata** | PostgreSQL | Tags, ratings, printed status, notes, categories |
| **Scan state** | PostgreSQL | Timestamps, hashes for incremental scan |
| **Thumbnails** | NFS (`.forgekeeper/thumbnails/`) | Generated cache; can be regenerated from source STLs |
| **Creator/model relationships** | PostgreSQL | Built from scanning, enriched by user |

---

## Supplementary Context for Codex

> This section provides key context for AI-assisted development (Codex, Copilot, etc.) working on the Forgekeeper codebase.

### Collection Statistics
- **Total size:** 3.9 TB across NFS
- **Files:** ~346,000 files in ~55,000 directories
- **MMF items:** 7,223 models from MyMiniFactory
- **Structure:** 72% follows MMFDownloader layout (165 unique creators)
- **Primary content:** Tabletop wargaming miniatures — Warhammer 40K, Age of Sigmar, D&D, Pathfinder, and general fantasy/sci-fi

### File Types Breakdown
| Type | Extension(s) | Prevalence | Notes |
|------|-------------|------------|-------|
| STL | `.stl` | ~80% | Primary print format, both ASCII and binary |
| OBJ | `.obj` | ~5% | Occasional, usually with `.mtl` |
| 3MF | `.3mf` | ~3% | Growing, often pre-configured for printing |
| Lychee | `.lys` | ~5% | Lychee Slicer project files (resin) |
| GCODE | `.gcode` | ~2% | Pre-sliced, printer-specific |
| Images | `.jpg`, `.png`, `.webp` | ~5% | Preview renders, painting guides |

### Existing Folder Structure Patterns

Forgekeeper must handle all of these on day one:

```
# Pattern 1: MMFDownloader (72% of collection)
sources/mmf/CreatorName/ModelName - 12345/
  ├── supported/
  ├── unsupported/
  ├── presupported/
  ├── lychee/
  ├── images/
  └── metadata.json          ← added by enhanced the MMF scraper plugin

# Pattern 2: Patreon monthly drops
sources/patreon/CreatorName/2025-06 June Release/
  ├── ModelA/
  │   ├── supported/
  │   └── unsupported/
  └── ModelB/

# Pattern 3: Generic (Thangs, Cults3D, Thingiverse, manual)
sources/{source}/CreatorName/ModelName/
  └── (flat files, possibly with subfolders)

# Pattern 4: Unsorted chaos
unsorted/
  ├── random_miniature.stl
  ├── Some Creator Pack v2.zip
  └── folder_of_stuff/
```

### Performance Requirements
- **Full scan** of 346K files: **< 30 minutes** (initial index)
- **Incremental scan** (detect new/changed): **< 2 minutes**
- **Search response:** **< 200ms** for any query (pg_trgm + proper indexing)
- **Page load:** **< 1 second** including thumbnail grid
- **Future scale:** Must handle **500K+ files** without degradation

### Developer Context
- **Owner:** The project owner — experienced C# developer
- **This is a personal project** — code clarity and maintainability over enterprise patterns
- **Preference:** Clean, pragmatic C# — not over-engineered, not sloppy
- **Testing approach:** Integration tests over mocks where practical (Testcontainers for PostgreSQL)

### MMF Scraper Plugin
- **the MMF scraper plugin** is a separate, private C# .NET 8 console app that downloads from MyMiniFactory
- **The MMF scraper plugin is loaded by Forgekeeper's plugin host** — no shared code, no shared packages, no API calls
- The **ONLY** interface is the filesystem: the MMF plugin writes files + `metadata.json` to `sources/mmf/`
- Forgekeeper reads those files through its `MmfSourceAdapter` — that's it
- If the MMF plugin is disabled, Forgekeeper still works (just no new MMF imports)

### Deployment Target
- **Kubernetes cluster** at `k8s.example.com` managed by Flux GitOps
- **NFS storage** mounted as a PV
- **PostgreSQL** via CNPG (CloudNativePG) operator on Longhorn storage
- **Ingress** at `forge.example.com` (Traefik or nginx)
- **Container registry:** GitHub Container Registry (ghcr.io)
- **CI/CD:** GitHub Actions → build Docker image → Flux auto-deploys

---

## Technology Decisions & Rationale

### Why C# / ASP.NET Core 9

| Consideration | Decision |
|---------------|----------|
| **Owner's primary language** | The owner writes C# professionally — ensures long-term maintainability |
| **Performance** | .NET 9 is competitive with Go/Rust for I/O-bound workloads; AOT compilation available |
| **Ecosystem** | Rich NuGet ecosystem for EF Core, file handling, image processing |
| **Container support** | First-class Docker/k8s support, small runtime-deps images |

> **Rejected:** Python/FastAPI (less preferred, runtime performance concerns at scale), Go (less comfortable for rapid iteration), Rust (overkill for a CRUD+files app).

### Why Minimal APIs over Controllers

| Consideration | Decision |
|---------------|----------|
| **Less boilerplate** | No controller classes, attributes, or conventions to learn |
| **Cleaner for this scope** | ~15 endpoints total — controllers add overhead without benefit |
| **Performance** | Slightly faster request pipeline (no MVC middleware) |
| **Modern .NET idiom** | Minimal APIs are the recommended approach for new .NET 9 projects of this scale |

> Minimal APIs group endpoints by feature in static classes (`ModelEndpoints.cs`, `ScanEndpoints.cs`), keeping the same organizational clarity as controllers without the ceremony.

### Why EF Core over Dapper

| Consideration | Decision |
|---------------|----------|
| **Migrations** | EF Core migrations manage schema evolution — critical for a long-lived project |
| **Relationships** | Creator → Model → Variant hierarchy maps naturally to EF navigation properties |
| **LINQ** | Complex search queries compose cleanly with IQueryable |
| **Pragmatism** | For the query complexity here, raw SQL adds maintenance burden without meaningful performance gain |

> **Escape hatch:** Raw SQL via `FromSqlInterpolated()` for any query where EF generates suboptimal SQL (e.g., pg_trgm similarity).

### Why PostgreSQL over SQLite

| Consideration | Decision |
|---------------|----------|
| **Scale** | 346K+ files generating millions of rows — SQLite starts to struggle with concurrent reads/writes |
| **pg_trgm** | PostgreSQL's trigram extension enables fast fuzzy search without an external search engine |
| **CNPG** | CloudNativePG operator provides automated backups, failover, and management in k8s |
| **Full-text search** | `tsvector` + GIN indexes for rich text search if needed beyond trigrams |
| **JSON support** | `jsonb` for storing `metadata.json` `extra` field natively |

> **Rejected:** SQLite (no pg_trgm, locking issues at scale), Elasticsearch (overkill, another service to maintain), MongoDB (no relational integrity for creator/model/variant hierarchy).

### Why Vue.js 3

| Consideration | Decision |
|---------------|----------|
| **Lightweight** | Smaller bundle than React/Angular for a relatively simple SPA |
| **Composition API** | Clean composable functions (`useSearch`, `useModels`) — natural fit for this project |
| **Three.js integration** | Well-documented Vue + Three.js patterns; `TresJS` or raw integration both work |
| **Learning curve** | Developers can pick up Vue quickly — simpler mental model than React hooks |

### Why Three.js for 3D Preview

| Consideration | Decision |
|---------------|----------|
| **STLLoader built-in** | `three/examples/jsm/loaders/STLLoader` — no additional libraries needed |
| **Industry standard** | Most mature WebGL library, massive community, excellent documentation |
| **OrbitControls** | Rotate, zoom, pan out of the box |
| **Performance** | Hardware-accelerated rendering handles even complex miniature STLs |

### Why Source-Parallel Directories

| Consideration | Decision |
|---------------|----------|
| **External tools keep working** | the MMF plugin writes to `sources/mmf/` — no Forgekeeper coordination needed |
| **Clean separation** | Each source has its own naming conventions and structure |
| **Easy backup/restore** | Can back up or restore a single source independently |
| **No migration needed** | Current MMFDownloader output IS the target structure |

> **Rejected:** Single flat hierarchy (breaks external tools), hash-based storage (opaque, can't browse manually), symlink farms (fragile on NFS).

### Why metadata.json Contract

| Consideration | Decision |
|---------------|----------|
| **Decouples downloaders from Forgekeeper** | Any tool can write the contract; Forgekeeper doesn't care what wrote it |
| **Portable** | JSON is universal — works from C#, Python, bash, anything |
| **Versionable** | `metadataVersion` field allows schema evolution without breaking old files |
| **Optional** | Forgekeeper works without it (falls back to folder name parsing) — it just works *better* with it |
| **One-way** | Downloaders write, Forgekeeper reads — no bidirectional sync complexity |

> This is the **ONLY** integration surface between external tools and Forgekeeper. No APIs, no databases, no message queues. Files on disk + a JSON sidecar. Simple, robust, debuggable.


---


---

## ID3-Inspired Metadata Fields

The metadata.json schema draws inspiration from the ID3v2.4 tag specification for MP3 files. STL files can't embed metadata (unlike MP3s with ID3 tags), so metadata.json serves as an external "ID3 tag" sidecar file. The following fields are inspired by specific ID3v2.4 frames.

### New Fields (added to metadata.json v1 schema)

#### `license` (inspired by ID3 TCOP — Copyright)

Critical for a library tool. "Can I sell prints of this?" must be answerable per-model.

```json
"license": {
  "type": "personal",
  "text": "For personal use only. Commercial printing not permitted.",
  "url": "https://source.com/model/12345/license"
}
```

| License Type | Meaning |
|-------------|---------|
| `personal` | Personal use only, no commercial prints |
| `commercial` | Commercial use allowed (selling prints OK) |
| `cc-by` | Creative Commons Attribution |
| `cc-by-nc` | Creative Commons Non-Commercial |
| `cc-by-sa` | Creative Commons ShareAlike |
| `cc0` | Public domain |
| `unknown` | License not specified by source |

#### `collection` (inspired by ID3 TPOS — Part of Set + TRCK — Track Number)

Models that belong together: Patreon monthly drops, MMF bundles, Kickstarter campaigns, army sets.

```json
"collection": {
  "name": "Dragon Army Bundle - March 2025",
  "externalId": "bundle-3147",
  "index": 3,
  "total": 12,
  "url": "https://source.com/bundle/3147"
}
```

Enables browsing "show me everything from the March 2025 Patreon drop" or "show all models in this bundle."

#### `sourceRating` (inspired by ID3 POPM — Popularimeter)

The rating ON the source platform (MMF stars, Thangs likes) is different from the user's personal rating in Forgekeeper. Keep both.

```json
"sourceRating": {
  "score": 4.8,
  "maxScore": 5.0,
  "votes": 127,
  "downloads": 5420,
  "likes": 892
}
```

ID3's POPM frame stores both the email of the rater AND the rating value, recognizing that different people rate the same content differently. Same principle: source rating vs. personal rating.

#### `fileHashes` (inspired by ID3 UFID — Unique File Identifier)

SHA-256 hash per file. Enables cross-source duplicate detection: "Is this the same STL I already have from Thingiverse?"

```json
"fileHashes": {
  "unsupported/dragon.stl": "sha256:a1b2c3d4e5f6...",
  "supported/dragon_supported.stl": "sha256:f6e5d4c3b2a1..."
}
```

ID3's UFID frame exists specifically for identifying the same content across different databases. For a 346K file collection spanning multiple sources, dedup is essential. Hashing at download time is cheap; hashing 346K files retroactively is expensive.

**Scrapers SHOULD compute and include file hashes.** Forgekeeper CAN compute them during scanning as a fallback, but it's much faster if the scraper does it at download time when the bytes are already in memory.

#### `relatedModels` (inspired by ID3 LINK — Linked Information)

"This base goes with that mini." "This mount pairs with that rider." Critical for army-building and set collecting.

```json
"relatedModels": [
  {
    "externalId": "67890",
    "name": "Dragon Knight Mount",
    "relation": "companion"
  },
  {
    "externalId": "67891",
    "name": "Dragon Army Base Set",
    "relation": "collection"
  }
]
```

| Relation Type | Meaning |
|-------------|---------|
| `companion` | Designed to go together (rider + mount, weapon + character) |
| `collection` | Part of the same themed set |
| `remix` | Modified version of another model |
| `alternate` | Different pose/variant of the same concept |
| `base` | Base/terrain designed for this model |

#### `printSettings` (no ID3 equivalent — domain-specific)

Since this is a 3D printing library, capture recommended print settings when available from the source:

```json
"printSettings": {
  "technology": "resin",
  "layerHeight": 0.05,
  "scale": "32mm",
  "supportsRequired": true,
  "estimatedPrintTime": "4h 30m",
  "estimatedResin": "45ml",
  "notes": "Print at 45 degree angle for best detail"
}
```

### Updated Full metadata.json Schema (v1 with ID3 fields)

```json
{
  "metadataVersion": 1,

  "source": "mmf",
  "externalId": "123456",
  "externalUrl": "https://...",

  "name": "Space Marine Captain",
  "description": "A highly detailed miniature...",
  "type": "object",
  "tags": ["warhammer", "40k", "space marine"],

  "creator": {
    "externalId": "789",
    "username": "sculptor",
    "displayName": "Awesome Sculptor Studio",
    "avatarUrl": "https://...",
    "profileUrl": "https://..."
  },

  "dates": {
    "created": "2025-06-15T10:00:00Z",
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
    "text": "Personal use only",
    "url": null
  },

  "collection": {
    "name": "Monthly Release March 2025",
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

  "relatedModels": [
    {
      "externalId": "67890",
      "name": "Companion Mount",
      "relation": "companion"
    }
  ],

  "printSettings": {
    "technology": "resin",
    "scale": "32mm",
    "supportsRequired": true
  },

  "images": [
    {
      "url": "https://...",
      "localPath": "images/preview.jpg",
      "type": "gallery"
    }
  ],

  "files": [
    {
      "filename": "captain.stl",
      "localPath": "unsupported/captain.stl",
      "size": 45678901,
      "variant": "unsupported",
      "downloadedAt": "2026-04-15T18:00:00Z"
    }
  ],

  "fileHashes": {
    "unsupported/captain.stl": "sha256:a1b2c3..."
  },

  "extra": {}
}
```



---

## Additional Metadata Fields

### `physicalProperties` — Computed by Forgekeeper

Geometry stats computed from the STL/OBJ during scanning. Answers "will this fit on my build plate?" without opening a slicer.

```json
"physicalProperties": {
  "boundingBox": { "x": 32.5, "y": 28.0, "z": 45.2 },
  "unit": "mm",
  "triangleCount": 245000,
  "isWatertight": true,
  "volume": 12.4
}
```

| Field | Type | Description |
|-------|------|-------------|
| `boundingBox` | object | X/Y/Z dimensions in model units |
| `unit` | string | "mm" (most STLs), "in", "unknown" |
| `triangleCount` | int | Number of triangles in mesh — affects slicer performance |
| `isWatertight` | bool | Whether the mesh is manifold/printable |
| `volume` | float | Volume in cubic units (useful for resin estimation) |

**Who writes it:** Forgekeeper computes this during scanning (not scrapers). If a scraper has this info from the source API, it can include it and Forgekeeper will skip recomputing. Stored per-variant since each file has different geometry.

### `printHistory` — Replaces boolean `printed` flag

Track every print attempt — different printers, materials, scales, successes, and failures.

```json
"printHistory": [
  {
    "date": "2026-03-15",
    "printer": "Elegoo Saturn 4 Ultra",
    "technology": "resin",
    "material": "Elegoo ABS-Like Grey",
    "layerHeight": 0.05,
    "scale": 1.0,
    "result": "success",
    "notes": "Perfect detail at 0.05mm. Printed at 45 degrees.",
    "duration": "4h 30m",
    "photos": ["prints/dragon_print_01.jpg", "prints/dragon_print_02.jpg"],
    "variant": "presupported/dragon_pre.stl"
  },
  {
    "date": "2026-04-01",
    "printer": "Bambu Lab A1",
    "technology": "fdm",
    "material": "PLA Grey",
    "layerHeight": 0.16,
    "scale": 2.0,
    "result": "failed",
    "notes": "Supports failed halfway through, spaghetti monster",
    "variant": "supported/dragon_supported.stl"
  }
]
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `date` | string | Yes | When printed (ISO date or datetime) |
| `printer` | string | No | Printer name |
| `technology` | string | No | "resin", "fdm", "sla", "msla" |
| `material` | string | No | Material used |
| `layerHeight` | float | No | Layer height in mm |
| `scale` | float | No | Scale factor (1.0 = original, 2.0 = doubled) |
| `result` | string | Yes | "success", "failed", "partial" |
| `notes` | string | No | Free-text notes about the print |
| `duration` | string | No | How long the print took |
| `photos` | string[] | No | Paths to photos of the printed model |
| `variant` | string | No | Which variant file was printed |

**Who writes it:** Forgekeeper only (user input via UI). This is user-generated data, not source data. Stored in PostgreSQL with the print history JSON, but also written back to metadata.json for portability.

The `printed` boolean on Model3D becomes a computed property: `printHistory.Any(p => p.Result == "success")`.

### `components` — Multi-Part Model Assembly

Warhammer models commonly have 10+ parts with weapon options, head swaps, and pose variants. This maps out which files are parts of the same model and which are alternatives.

```json
"components": [
  {
    "name": "Body",
    "file": "unsupported/body.stl",
    "required": true,
    "group": null
  },
  {
    "name": "Weapon Option A - Power Sword",
    "file": "unsupported/power_sword.stl",
    "required": false,
    "group": "weapon"
  },
  {
    "name": "Weapon Option B - Thunder Hammer",
    "file": "unsupported/thunder_hammer.stl",
    "required": false,
    "group": "weapon"
  },
  {
    "name": "Head - Helmet",
    "file": "unsupported/head_helmet.stl",
    "required": false,
    "group": "head"
  },
  {
    "name": "Head - Bare",
    "file": "unsupported/head_bare.stl",
    "required": false,
    "group": "head"
  },
  {
    "name": "Base 32mm",
    "file": "unsupported/base_32mm.stl",
    "required": true,
    "group": null
  }
]
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `name` | string | Yes | Human-readable part name |
| `file` | string | Yes | Path relative to model directory |
| `required` | bool | Yes | Must be printed (body, base) vs. optional (weapon swap) |
| `group` | string? | No | Group name for alternatives. Parts in the same group are interchangeable — pick one from "weapon", one from "head", etc. |

**Who writes it:** Ideally the scraper, if the source API provides assembly info. More commonly, the user manually organizes parts via the Forgekeeper UI. Some creators include assembly instructions or part lists that could be parsed.

**UX impact:** The model detail page can show a "Build Your Model" view with required parts checked and option groups as radio buttons. "Pick a weapon: Sword / Hammer / Axe." Much better than a flat list of 15 unnamed STLs.

### `originalFilename` — Added to `files[]` entries

Preserve the original filename from the source before sanitization/renaming during import.

```json
"files": [
  {
    "filename": "captain_supported.stl",
    "originalFilename": "SM_Captain_V2_FINAL_supported (1).stl",
    "localPath": "supported/captain_supported.stl",
    "size": 45678901,
    "variant": "supported"
  }
]
```

**Why:** File renaming during import/download loses information. The original filename often contains version numbers ("V2"), variant hints ("FINAL", "supported"), and creator naming conventions that help with:
- Matching back to the source for re-download
- Detecting which version of a file you have
- Debugging when something doesn't look right ("why is this file only 500KB?")

**Who writes it:** Scrapers, at download time. If the scraper renames files for filesystem safety, it records the original name. Forgekeeper records it during the unsorted/ import flow if the user renames files.


### ID3 Design Principles Applied

| ID3 Principle | How We Apply It |
|--------------|-----------------|
| **Extensible frames** — unknown frames are skipped, not rejected | `extra` field + Forgekeeper ignores unknown top-level fields |
| **Version tagging** — ID3v1 vs v2 identified by header | `metadataVersion` field for future schema evolution |
| **Multiple values** — ID3v2.4 allows multi-value text frames | `tags[]`, `relatedModels[]`, `files[]` are all arrays |
| **Encoding declaration** — ID3v2 specifies text encoding per frame | JSON = UTF-8 always, no encoding issues |
| **Backward compatibility** — ID3v2.4 readers handle v2.3 tags | All new fields are optional, v1 readers skip what they don't know |
| **Private frames** — PRIV frame for app-specific data | `extra` field serves the same purpose |
| **Unique identifier** — UFID for cross-database matching | `fileHashes` for cross-source dedup |
| **Separation of concerns** — performer vs. composer vs. band | `creator` vs. `collection` vs. `source` are distinct |


## Writing a Source Scraper

Any tool that downloads 3D printing files can integrate with Forgekeeper. The tool doesn't need to know anything about Forgekeeper's database, API, or internals. Just follow this contract.

### Minimum Viable Scraper (Bash)

```bash
#!/bin/bash
SOURCE="coolsite"
BASE="/mnt/3dprinting/sources/$SOURCE"
CREATOR="SomeCreator"
MODEL="Awesome Dragon"
MODEL_DIR="$BASE/$CREATOR/$MODEL"

mkdir -p "$MODEL_DIR/unsupported"
wget -O "$MODEL_DIR/unsupported/dragon.stl" "https://coolsite.com/download/12345"

cat > "$MODEL_DIR/metadata.json" << EOF
{
  "metadataVersion": 1,
  "source": "coolsite",
  "externalId": "12345",
  "name": "Awesome Dragon",
  "creator": { "displayName": "SomeCreator" },
  "dates": { "downloaded": "$(date -u +%Y-%m-%dT%H:%M:%SZ)" },
  "files": [{ "filename": "dragon.stl", "localPath": "unsupported/dragon.stl", "variant": "unsupported" }]
}
EOF
```

That's it. Forgekeeper finds it on the next scan.

### Directory Structure Contract

```
sources/{source-slug}/
  {CreatorName}/
    {ModelName}/
      metadata.json
      supported/
        model_supported.stl
      unsupported/
        model.stl
      presupported/
        model_pre.stl
      lychee/
        model.lys
      images/
        preview.jpg
```

**Rules:**
- `{source-slug}`: lowercase, no spaces, URL-safe (mmf, thangs, patreon, cults3d)
- `{CreatorName}`: creator's display name, sanitized for filesystem
- `{ModelName}`: human-readable, include source ID if available (e.g., "Dragon Knight - 12345")
- Variant subfolders optional -- Forgekeeper auto-detects from filenames if flat
- `images/` for previews -- Forgekeeper uses these instead of generating thumbnails

### Variant Detection

Set `files[].variant` if your scraper knows the type:

| Variant | When to use |
|---------|-------------|
| `unsupported` | Raw model, no supports (default) |
| `supported` | Has print supports added |
| `presupported` | Pre-supported by creator |
| `lychee` | Lychee slicer project (.lys) |
| `other` | Anything else |

If not set, Forgekeeper auto-detects from subfolder names, filename patterns, and file extensions.

### Re-download / Update Flow

If your scraper supports updating existing models:

1. **Read existing metadata.json** before downloading
2. **Compare `dates.updated`** -- skip if unchanged
3. **Check `files[]` array** -- only download files not already listed
4. **Merge new files** into existing metadata.json
5. **Respect existing directory structure** -- place new files in correct variant subfolders

### C# Scraper Template

```csharp
public static async Task WriteModel(string basePath, string source,
    string creatorName, string modelName, string externalId,
    List<(string filename, string variantType, byte[] data)> files)
{
    var modelDir = Path.Combine(basePath, "sources", source,
        SanitizePath(creatorName), SanitizePath(modelName));

    var metaPath = Path.Combine(modelDir, "metadata.json");
    SourceMetadata existing = File.Exists(metaPath)
        ? JsonSerializer.Deserialize<SourceMetadata>(
            await File.ReadAllTextAsync(metaPath))
        : new SourceMetadata();

    var existingFiles = new HashSet<string>(
        existing.Files?.Select(f => f.Filename) ?? Array.Empty<string>());

    var newFiles = new List<FileEntry>();
    foreach (var (filename, variant, data) in files)
    {
        if (existingFiles.Contains(filename)) continue;

        var variantDir = Path.Combine(modelDir, variant);
        Directory.CreateDirectory(variantDir);
        await File.WriteAllBytesAsync(
            Path.Combine(variantDir, filename), data);

        newFiles.Add(new FileEntry {
            Filename = filename,
            LocalPath = $"{variant}/{filename}",
            Size = data.Length,
            Variant = variant,
            DownloadedAt = DateTime.UtcNow
        });
    }

    existing.MetadataVersion = 1;
    existing.Source = source;
    existing.ExternalId = externalId;
    existing.Name = modelName;
    existing.Creator ??= new CreatorInfo { DisplayName = creatorName };
    existing.Dates ??= new DateInfo();
    existing.Dates.Downloaded = DateTime.UtcNow;
    existing.Files = (existing.Files ?? new List<FileEntry>())
        .Concat(newFiles).ToList();

    await File.WriteAllTextAsync(metaPath,
        JsonSerializer.Serialize(existing,
            new JsonSerializerOptions { WriteIndented = true }));
}
```

### Python Scraper Template

```python
import json, os, datetime

def write_model(base_path, source, creator_name, model_name,
                external_id, files_list):
    model_dir = os.path.join(base_path, "sources", source,
                             sanitize(creator_name), sanitize(model_name))
    meta_path = os.path.join(model_dir, "metadata.json")

    existing = {}
    if os.path.exists(meta_path):
        with open(meta_path) as f:
            existing = json.load(f)

    existing_files = {f["filename"] for f in existing.get("files", [])}
    new_files = []

    for filename, variant, data in files_list:
        if filename in existing_files:
            continue
        variant_dir = os.path.join(model_dir, variant)
        os.makedirs(variant_dir, exist_ok=True)
        with open(os.path.join(variant_dir, filename), "wb") as f:
            f.write(data)
        new_files.append({
            "filename": filename,
            "localPath": f"{variant}/{filename}",
            "size": len(data),
            "variant": variant,
            "downloadedAt": datetime.datetime.utcnow().isoformat() + "Z"
        })

    metadata = {
        "metadataVersion": 1,
        "source": source,
        "externalId": external_id,
        "name": model_name,
        "creator": existing.get("creator", {"displayName": creator_name}),
        "dates": {**existing.get("dates", {}),
                  "downloaded": datetime.datetime.utcnow().isoformat() + "Z"},
        "files": existing.get("files", []) + new_files,
        "extra": existing.get("extra", {})
    }
    with open(meta_path, "w") as f:
        json.dump(metadata, f, indent=2)

def sanitize(name):
    return "".join(c if c.isalnum() or c in " -_.()" else "-" for c in name)
```

### Testing Your Scraper

1. Create a few test model directories with your scraper
2. Run Forgekeeper's scanner: `POST /api/v1/scan`
3. Check models appear: `GET /api/v1/models`
4. Verify metadata in the UI
5. Test re-download: run scraper again, confirm no duplicates

### Forgekeeper Writes metadata.json Too

When a user manually imports files through the Forgekeeper UI (unsorted/ flow), Forgekeeper writes a metadata.json to the destination directory. This means:

- **Database-free recovery:** Re-scanning rebuilds the full library including manually imported items
- **Portable metadata:** Your curation work (tags, ratings, categories) survives a database rebuild
- **Consistent contract:** Every model in every source directory ends up with a metadata.json, regardless of how it got there

For items that arrived WITH a metadata.json from a scraper, Forgekeeper does NOT overwrite it. It only writes metadata.json for items that don't already have one.
