# Forgekeeper Documentation

**Forgekeeper** is a self-hosted digital asset manager for massive 3D printing collections. Built for hobbyists who accumulate files from multiple sources (MyMiniFactory, Thangs, Patreon, Cults3D, Thingiverse) and need to actually find things.

## Key Features

- **Unified library** across multiple source platforms with source-parallel directory structure
- **Smart variant handling** — supported/unsupported/presupported files grouped under one model
- **Import pipeline** — drop files in `unsorted/`, auto-detect and sort them
- **Full-text search** with PostgreSQL pg_trgm fuzzy matching
- **3D STL preview** in the browser via Three.js
- **Thumbnail generation** (WebP) for visual browsing
- **Tagging, rating, categorization** — game system, scale, print history, notes
- **Plugin system** for scraping external library platforms
- **MCP integration** for AI-assisted collection management
- **Built for scale** — handles 300K+ files without breaking a sweat

## Documentation

| Page | Description |
|------|-------------|
| [Getting Started](getting-started.md) | Installation, first run, adding sources |
| [Architecture](architecture.md) | System design, data model, tech stack |
| [API Reference](api-reference.md) | Complete REST API documentation |
| [Configuration](configuration.md) | Environment variables, appsettings, security |
| [Plugin Development](plugin-development.md) | Building scraper plugins with the SDK |
| [Deployment](deployment.md) | Docker Compose, Kubernetes, NFS, CNPG |
| [Contributing](contributing.md) | Dev setup, testing, code style |

## Quick Start

```bash
git clone https://github.com/inxaos-repo/forgekeeper.git
cd forgekeeper
cp .env.example .env   # Edit LIBRARY_PATH
docker compose up -d
# Open http://localhost:5000
```

→ [Full Getting Started guide](getting-started.md)

## Links

- [GitHub Repository](https://github.com/inxaos-repo/forgekeeper)
- [Full Specification](../SPEC.md)
- [License](../LICENSE) (MIT)
