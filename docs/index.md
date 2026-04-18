# Forgekeeper Documentation

**Forgekeeper** is a self-hosted digital asset manager for massive 3D printing collections — "Plex for STL files." Built for hobbyists who accumulate files from multiple sources (MyMiniFactory, Thangs, Patreon, Cults3D, Thingiverse) and need to actually find things.

Once deployed, access your instance at `http://localhost:5000` (or your configured hostname).

## Key Features

- **Unified library** across multiple source platforms with source-parallel directory structure
- **Smart variant handling** — supported/unsupported/presupported files grouped under one model
- **Import pipeline** — drop files in `unsorted/`, auto-detect and sort them
- **Full-text search** with PostgreSQL pg_trgm fuzzy matching + all filters (source, category, game system, scale, rating, printed, license, collection, tags)
- **3D STL preview** in the browser via Three.js
- **Thumbnail generation** (WebP) for visual browsing — stl-thumb renders 16,600+ thumbnails
- **Tagging, rating, categorization** — game system, scale, print history, notes, collections
- **Plugin system** — isolated AssemblyLoadContext, NFS hot reload, per-plugin sync lock
- **MMF scraper plugin** — FlareSolverr Cloudflare bypass, cookie auth, 7,230+ model sync
- **Bulk management** — bulk tag, categorize, metadata update, creator reassignment
- **Rename/move with disk operations** — template-based preview + atomic move
- **Print history** (JSONB), related models, components, print settings
- **Metadata writeback** — user edits sync back to `metadata.json` for database-free recovery
- **MCP integration** — 18 tools for AI assistant collection management
- **Prometheus `/metrics` endpoint** + Grafana-ready
- **SSE sync progress streaming** — real-time plugin sync updates
- **241 backend tests** + **27 E2E tests** (Playwright) in GHA CI
- **Built for scale** — handles 300K+ files without breaking a sweat

## Documentation

| Page | Description |
|------|-------------|
| [Getting Started](getting-started.md) | Installation, first run, adding sources, MMF plugin setup |
| [Architecture](architecture.md) | System design, data model, plugin host, MCP, metadata contract |
| [API Reference](api-reference.md) | Complete REST API documentation (50+ endpoints) |
| [Configuration](configuration.md) | Environment variables, appsettings, security |
| [Plugin Development](plugin-development.md) | Building scraper plugins with the SDK |
| [Deployment](deployment.md) | Docker Compose, Kubernetes + Flux, NFS, CNPG, FlareSolverr |
| [Contributing](contributing.md) | Dev setup, 241 tests, E2E tests, migrations, code style |

## Quick Start

```bash
git clone https://github.com/your-org/forgekeeper.git
cd forgekeeper
cp .env.example .env   # Edit LIBRARY_PATH and FORGEKEEPER_ENCRYPTION_KEY
docker compose up -d
# Open http://localhost:5000
```

→ [Full Getting Started guide](getting-started.md)

## Links

- [GitHub Repository](https://github.com/your-org/forgekeeper)
- [Full Specification](../SPEC.md)
- [License](../LICENSE) (MIT)
