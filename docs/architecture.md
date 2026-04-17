# Architecture Overview

## High-Level Architecture

```
┌─────────────────────────────────────────────────┐
│       External Tools (scraper plugins)          │
│  Write files + metadata.json to sources/        │
└──────────────────┬──────────────────────────────┘
                   │ metadata.json contract
┌──────────────────▼──────────────────────────────┐
│  Forgekeeper                                     │
│  ┌──────────────────────────────────────────┐   │
│  │  ASP.NET Core 9 API (Minimal APIs)       │   │
│  │  ├── Model Endpoints (CRUD, search)      │   │
│  │  ├── Creator Endpoints                   │   │
│  │  ├── Tag Endpoints                       │   │
│  │  ├── Source Endpoints                    │   │
│  │  ├── Import Endpoints                    │   │
│  │  ├── Plugin Endpoints                    │   │
│  │  ├── MCP Endpoints                       │   │
│  │  └── Stats / Health                      │   │
│  └──────────────────────────────────────────┘   │
│  ┌──────────────┐ ┌──────────────┐ ┌─────────┐ │
│  │ Scanner Svc  │ │ Thumbnail    │ │ Plugin  │ │
│  │ (background) │ │ Worker (bg)  │ │ Host    │ │
│  └──────────────┘ └──────────────┘ └─────────┘ │
│  ┌──────────────┐ ┌──────────────┐              │
│  │ Search Svc   │ │ Import Svc   │              │
│  │ (pg_trgm)    │ │ (unsorted→)  │              │
│  └──────────────┘ └──────────────┘              │
│  ┌──────────────────────────────────────────┐   │
│  │  Vue.js 3 SPA (Three.js STL viewer)      │   │
│  └──────────────────────────────────────────┘   │
├─────────────────────────────────────────────────┤
│  PostgreSQL 16 (metadata, tags, search)         │
│  NFS / Local Storage (files, thumbnails)        │
└─────────────────────────────────────────────────┘
```

## Tech Stack

| Layer | Technology |
|-------|-----------|
| **Backend** | C# / ASP.NET Core 9 (Minimal APIs) |
| **Database** | PostgreSQL 16 with `pg_trgm` extension |
| **ORM** | Entity Framework Core 9 (Npgsql, snake_case naming) |
| **Frontend** | Vue.js 3 (Composition API) + Vite + Tailwind CSS |
| **3D Preview** | Three.js with STLLoader |
| **Thumbnails** | stl-thumb (Rust CLI) → WebP output |
| **Container** | Docker (multi-stage .NET 9 + Node 22 build) |
| **Logging** | Serilog (console + structured) |
| **CI/CD** | GitHub Actions → GHCR container images |

## Component Overview

### API (`src/Forgekeeper.Api/`)

The REST API layer built with ASP.NET Core Minimal APIs. Routes are organized into endpoint groups:

- **ModelEndpoints** — CRUD, search, print history, components, related models, bulk operations, duplicates
- **CreatorEndpoints** — list creators, get creator details and models
- **TagEndpoints** — list tags, add/remove tags from models
- **SourceEndpoints** — manage source directories
- **ScanEndpoints** — trigger and monitor scans
- **ImportEndpoints** — process unsorted files, manage import queue
- **PluginEndpoints** — plugin management, config, sync, auth
- **StatsEndpoints** — collection and creator statistics
- **VariantEndpoints** — file download, variant thumbnails
- **MCP** — Model Context Protocol integration for AI tools

Middleware:
- **ApiKeyMiddleware** — optional API key authentication via `X-Api-Key` header

### Scanner Service (`Infrastructure/Services/FileScannerService.cs`)

Background service that indexes the filesystem into the database:

- Walks source directories recursively
- Reads `metadata.json` files for rich metadata
- Detects file variants (supported, unsupported, presupported, etc.)
- Computes file hashes for deduplication
- Supports both full and incremental scanning modes
- Reports progress via `ScanProgress` DTO

### Thumbnail Worker (`Api/BackgroundServices/ThumbnailWorker.cs`)

Background hosted service that generates thumbnails:

- Uses `stl-thumb` (Rust CLI) for STL → WebP rendering
- Processes models that lack thumbnails
- Configurable size and format via settings
- Stores thumbnails in `.thumbnails/` under the base path

### Plugin Host (`Infrastructure/Services/PluginHostService.cs`)

Manages scraper plugins that integrate with external platforms:

- Loads plugin DLLs from the plugins directory
- Provides `PluginContext` with HttpClient, logger, config, and token store
- Manages sync operations with progress tracking
- Handles OAuth callbacks for browser-based auth flows
- Stores plugin configuration and tokens encrypted in the database

### Frontend (`src/Forgekeeper.Web/`)

Vue.js 3 single-page application:

- Composition API with `<script setup>`
- Three.js STL viewer for in-browser 3D model preview
- Tailwind CSS for styling
- Vite for build tooling
- Served as static files from the API's `wwwroot/`

## Data Model

### Core Entities

```
Creator (1) ──── (N) Model3D (1) ──── (N) Variant
                      │                      │
                      ├── Tags (M:N)         └── PhysicalProperties (JSONB)
                      ├── PrintHistory (JSONB)
                      ├── Components (JSONB)
                      ├── PrintSettings (JSONB)
                      └── RelationsFrom/To (self M:N via ModelRelation)
                      
Source (1) ──── (N) Model3D

PluginConfig ── stores per-plugin key/value configuration
ImportQueueItem ── tracks unsorted file processing
ScanState ── tracks per-directory scan state
```

### Entity Descriptions

| Entity | Description |
|--------|-------------|
| **Model3D** | A 3D model with metadata, belonging to a creator and source. Contains JSONB columns for print history, components, and print settings. |
| **Creator** | A model creator/sculptor. Has a source type and optional external profile URL. |
| **Variant** | A specific file within a model (e.g., supported STL, unsupported STL, presupported). Tracks file path, size, hash, and physical properties. |
| **Tag** | A tag applied to models. Many-to-many relationship. Tags have an optional `source` field ("scraper" vs "user"). |
| **Source** | A configured source directory with adapter type and auto-scan setting. |
| **ModelRelation** | Self-referencing many-to-many for model relationships (collection, companion, remix, alternate, base). |
| **PluginConfig** | Per-plugin configuration stored encrypted in the database. |
| **ImportQueueItem** | Tracks files in the unsorted directory through the import pipeline. |

### Key Enums

| Enum | Values |
|------|--------|
| **SourceType** | `Mmf`, `Thangs`, `Patreon`, `Cults3d`, `Thingiverse`, `Manual` |
| **VariantType** | `Unsupported`, `Supported`, `Presupported`, `LycheeProject`, `ChituboxProject`, `Gcode`, `PrintProject`, `PreviewImage`, `Other` |
| **FileType** | `Stl`, `Obj`, `Threemf`, `Lys`, `Ctb`, `Cbddlp`, `Gcode`, `Sl1`, `Png`, `Jpg`, `Webp`, `Other` |
| **ImportStatus** | `Pending`, `AutoSorted`, `AwaitingReview`, `Confirmed`, `Failed` |
| **AcquisitionMethod** | `Unknown`, `Purchase`, `Subscription`, `Free`, `Campaign`, `Gift` |

## metadata.json Contract

The `metadata.json` file is the integration contract between external tools and Forgekeeper — the "ID3 tag" for 3D model directories.

### Who Writes What

| Field | Written by | Notes |
|-------|-----------|-------|
| `source`, `externalId`, `creator`, `dates.*`, `files[]` | Scraper/downloader | Forgekeeper MUST NOT overwrite |
| `tags` | Both | Union merge — both add, neither removes |
| `license` | Both | User-set value takes precedence |
| `collection`, `printSettings`, `printHistory`, `components`, `relatedModels`, `physicalProperties` | Forgekeeper | User-owned fields written back |

### Minimal Example

```json
{
  "metadataVersion": 1,
  "source": "mmf",
  "externalId": "123456",
  "name": "Dragon Miniature",
  "creator": {
    "displayName": "AwesomeSculptor",
    "username": "awesome-sculptor"
  },
  "dates": {
    "downloaded": "2026-04-15T18:00:00Z"
  },
  "files": [
    {
      "filename": "dragon.stl",
      "localPath": "unsupported/dragon.stl",
      "variant": "unsupported"
    }
  ]
}
```

This enables **database-free recovery** — re-scanning the filesystem rebuilds the full library including user edits.

## File Storage Layout

```
{LIBRARY_PATH}/
├── sources/
│   ├── {source-slug}/                  # e.g., mmf/, thangs/, patreon/
│   │   └── {CreatorName}/
│   │       └── {ModelName}/
│   │           ├── supported/          # Pre-supported STLs
│   │           ├── unsupported/        # Raw STLs
│   │           ├── presupported/       # Presupported variants
│   │           ├── images/             # Preview images
│   │           └── metadata.json       # Model metadata
├── unsorted/                           # Import drop zone
└── .forgekeeper/                       # Auto-created
    └── .thumbnails/                    # Generated WebP thumbnails
```

## Database Schema Overview

PostgreSQL 16 with EF Core snake_case naming convention. Key tables:

| Table | Description |
|-------|-------------|
| `models` | Core model metadata, JSONB columns for print_history, components, print_settings |
| `creators` | Creator/sculptor profiles |
| `variants` | Individual files within models, with hash and physical properties |
| `tags` | Tag definitions |
| `model_tags` | Many-to-many join table |
| `model_relations` | Self-referencing model relationships |
| `sources` | Configured source directories |
| `plugin_configs` | Encrypted plugin configuration |
| `import_queue` | Import pipeline state |
| `scan_states` | Per-directory scan tracking |

The `pg_trgm` extension is required for fuzzy text search and is created automatically via `init-db.sql`.
