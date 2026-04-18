# Forgekeeper — 3D Print File Manager

A self-hosted digital asset manager for massive 3D printing collections. **Plex for STL files** — built for hobbyists who accumulate files from multiple sources and need to actually find things.

## Current Status

Forgekeeper is actively used in production managing a large personal collection:

| Metric | Value |
|--------|-------|
| Models indexed | **8,800+** |
| Creators | **170+** |
| File variants | **200,000+** |
| Library size | **3.6 TB** |
| Backend tests | **330+** |
| E2E tests (Playwright) | **27** |

*These numbers are from a real deployment — not synthetic benchmarks.*

## Features

- **Unified library** across multiple sources (MyMiniFactory, Thangs, Patreon, Cults3D, Thingiverse, manual)
- **Smart variant handling** — supported/unsupported/presupported versions grouped under one model
- **Source-parallel directories** — each source keeps its own folder structure; Forgekeeper provides the unified view
- **Import pipeline** — drop files in `unsorted/`, auto-detect what they are, confirm and sort
- **Full-text search** with PostgreSQL pg_trgm fuzzy matching + all filters (source, category, game system, scale, rating, printed, license, collection, tags)
- **3D STL preview** in the browser (Three.js)
- **Thumbnail generation** — stl-thumb → WebP, auto-queued for all STL files
- **Tagging, rating, categorization** — game system, scale, printed status, notes, collections
- **Plugin system** — isolated AssemblyLoadContext, NFS-based hot reload, per-plugin config/auth
- **MMF scraper plugin** — FlareSolverr Cloudflare bypass, cookie auth, raw JSON API, 7,230+ model library sync
- **Bulk management** — bulk tag, categorize, metadata update, creator reassignment across hundreds of models
- **Rename/move with disk operations** — template-based rename preview + atomic move
- **Print history** (JSONB), related models, components, print settings
- **Metadata writeback** — user edits sync back to `metadata.json` for database-free recovery
- **MCP interface** — 18 tools for AI assistant integration (search, update, bulk ops, analytics)
- **Prometheus `/metrics` endpoint** for monitoring
- **SSE sync progress streaming** — real-time plugin sync progress
- **Built for scale** — handles 300K+ files without breaking a sweat

## Architecture

```
┌──────────────────────────────────────────────────────────┐
│  Plugin Host (AssemblyLoadContext isolation)              │
│  ├── MMF Scraper Plugin (FlareSolverr CF bypass)         │
│  └── [custom plugins — drop DLL, restart, done]          │
│  Writes files + metadata.json to sources/                │
└────────────────────────┬─────────────────────────────────┘
                         │ metadata.json contract
┌────────────────────────▼─────────────────────────────────┐
│  Forgekeeper                                              │
│  ┌─────────────────────────────────────────────────────┐ │
│  │  ASP.NET Core 9 API (Minimal APIs)                  │ │
│  │  ├── Models / Creators / Tags / Sources             │ │
│  │  ├── Import / Scan / Variants                       │ │
│  │  ├── Plugin management + SSE progress               │ │
│  │  ├── MCP interface (18 tools)                       │ │
│  │  └── Stats / Health / Prometheus /metrics           │ │
│  └─────────────────────────────────────────────────────┘ │
│  ┌──────────────┐ ┌──────────────┐ ┌───────────────────┐ │
│  │ Scanner Svc  │ │ Thumbnail    │ │ Plugin Host Svc   │ │
│  │ (background) │ │ Worker (bg)  │ │ (sync lock+retry) │ │
│  └──────────────┘ └──────────────┘ └───────────────────┘ │
│  ┌──────────────┐ ┌──────────────┐                       │
│  │ Search Svc   │ │ Import Svc   │                       │
│  │ (pg_trgm)    │ │ (unsorted→)  │                       │
│  └──────────────┘ └──────────────┘                       │
│  ┌─────────────────────────────────────────────────────┐ │
│  │  Vue.js 3 SPA (Three.js STL viewer)                 │ │
│  └─────────────────────────────────────────────────────┘ │
├──────────────────────────────────────────────────────────┤
│  PostgreSQL 16 via CNPG (metadata, tags, search)         │
│  NFS Storage (files, thumbnails)                         │
└──────────────────────────────────────────────────────────┘
```

## Tech Stack

| Layer | Technology |
|-------|-----------|
| **Backend** | C# / ASP.NET Core 9 (Minimal APIs) |
| **Database** | PostgreSQL 16 with `pg_trgm` extension |
| **ORM** | Entity Framework Core 9 (Npgsql) |
| **Frontend** | Vue.js 3 (Composition API) + Vite + Tailwind CSS |
| **3D Preview** | Three.js with STLLoader |
| **Thumbnails** | stl-thumb (Rust CLI) → WebP |
| **Container** | Docker (multi-stage .NET 9 + Node 22 build) |
| **CI/CD** | GitHub Actions → GHCR container images |
| **Deployment** | Flux GitOps → Kubernetes + CNPG |

## Quick Start

### Prerequisites
- Docker and Docker Compose
- Your 3D printing file collection on a local path

### Run with Docker Compose

```bash
# Clone the repo
git clone https://github.com/your-org/forgekeeper.git
cd forgekeeper

# Configure your library path
cp .env.example .env
# Edit .env: set LIBRARY_PATH and FORGEKEEPER_ENCRYPTION_KEY

# Start everything (Forgekeeper + PostgreSQL + FlareSolverr)
docker compose up -d

# Access the web UI
open http://localhost:5000
```

The scanner runs automatically on startup. Initial scan of a large collection may take a few minutes.

### Enable MyMiniFactory Library Sync

FlareSolverr is included in `docker-compose.yml` and starts automatically.

1. Go to **Plugins** tab in the UI
2. Configure your MMF username and password
3. Click **Sync** — FlareSolverr handles the Cloudflare bypass automatically

### Run for Development

```bash
# Backend
cd src/Forgekeeper.Api && dotnet run

# Frontend (separate terminal)
cd src/Forgekeeper.Web && npm install && npm run dev

# Database
docker compose up postgres -d
```

See the [Dev Environment guide](DEV-ENVIRONMENT.md) for the full dev container workflow.

## Directory Structure

```
/your/3d-printing/collection/
├── sources/
│   ├── mmf/              # MyMiniFactory downloads
│   │   └── Creator/
│   │       └── Model Name/
│   │           ├── supported/
│   │           ├── unsupported/
│   │           ├── images/
│   │           └── metadata.json
│   ├── thangs/
│   ├── patreon/
│   ├── cults3d/
│   ├── thingiverse/
│   └── manual/
├── unsorted/             # Drop zone for auto-import
└── .forgekeeper/         # Thumbnails and cache (auto-created)
```

## Integration Contract: metadata.json

Any external tool that writes a `metadata.json` alongside downloaded models enables rich import. Forgekeeper also **writes back** user-owned fields (tags, ratings, print history, components) to keep the filesystem as the ground truth.

```json
{
  "metadataVersion": 1,
  "source": "mmf",
  "externalId": "123456",
  "name": "Model Name",
  "creator": {
    "displayName": "Creator Name",
    "username": "creator-slug"
  },
  "dates": { "downloaded": "2026-04-15T18:00:00Z" },
  "files": [
    { "filename": "model.stl", "localPath": "unsupported/model.stl", "variant": "unsupported" }
  ]
}
```

This enables **database-free recovery** — re-scanning the filesystem rebuilds the full library including user edits.

## Plugin System

Plugins implement `ILibraryScraper` from the `Forgekeeper.PluginSdk` package and are loaded via isolated `AssemblyLoadContext`. Drop a DLL in the `plugins/` directory and restart — Forgekeeper discovers and loads it automatically.

The MMF plugin demonstrates the full pattern: FlareSolverr-based Cloudflare bypass, cookie auth, raw JSON API parsing, and incremental sync with per-plugin `SemaphoreSlim` lock and exponential backoff retry.

```bash
# See what plugins are loaded
curl http://localhost:5000/api/v1/plugins

# Trigger a sync
curl -X POST http://localhost:5000/api/v1/plugins/mmf/sync

# Stream progress (SSE)
curl http://localhost:5000/api/v1/plugins/mmf/progress
```

See the [Plugin Development Guide](docs/plugin-development.md) for full details.

## MCP Integration

Forgekeeper exposes an MCP (Model Context Protocol) interface with 18 tools for AI assistant integration:

| Category | Tools |
|----------|-------|
| Read | `search`, `getModel`, `getCreator`, `listSources`, `stats`, `findDuplicates`, `findUntagged`, `recent` |
| Write | `tagModel`, `updateModel`, `markPrinted`, `setComponents`, `linkModels`, `bulkUpdate`, `triggerSync` |
| Analysis | `collectionReport`, `healthCheck`, `printHistory` |

```bash
# List available tools
curl http://localhost:5000/mcp/tools

# Invoke a tool
curl -X POST http://localhost:5000/mcp/invoke \
  -H "Content-Type: application/json" \
  -d '{"tool": "search", "arguments": {"query": "dragon", "category": "Fantasy"}}'
```

## Monitoring

Forgekeeper exports Prometheus metrics at `GET /metrics`:

- Total models, creators, files, thumbnails
- Active scan/sync operations
- Per-plugin sync counters and last-run timestamps
- Application health via `GET /health`

## API

Base URL: `/api/v1` — full docs at [docs/api-reference.md](docs/api-reference.md)

**Models:** search, detail, patch, delete, print history, components, thumbnail, related, rename/move, bulk ops, duplicates  
**Creators:** list, detail, models  
**Tags:** list all tags, per-model add/remove  
**Sources:** list, create, delete  
**Scan:** full, incremental, status, untracked  
**Import:** process, queue, confirm, dismiss  
**Plugins:** list, config, sync, auth, SSE progress, status, manifest upload  
**Stats:** collection stats  
**MCP:** tool list, tool invoke  
**Metrics:** Prometheus `/metrics`, health `/health`

## Documentation

| Page | Description |
|------|-------------|
| [Getting Started](docs/getting-started.md) | Installation, first run, plugin setup |
| [Architecture](docs/architecture.md) | System design, data model, metadata contract |
| [API Reference](docs/api-reference.md) | Complete REST API documentation |
| [Configuration](docs/configuration.md) | Environment variables and settings |
| [Plugin Development](docs/plugin-development.md) | Building scraper plugins with the SDK |
| [Deployment](docs/deployment.md) | Docker, Kubernetes, Flux, NFS, CNPG |
| [Contributing](docs/contributing.md) | Dev setup, testing, migrations, code style |

## License

MIT

## Contributing

Pull requests welcome. See [Contributing Guide](docs/contributing.md) and [SPEC.md](SPEC.md).
