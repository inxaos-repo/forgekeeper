# Getting Started

## Prerequisites

- **Docker** and **Docker Compose** (v2+)
- A 3D printing file collection on a local or network path
- (Optional) PostgreSQL 16 if running without Docker

## Installation via Docker Compose

### 1. Clone the Repository

```bash
git clone https://github.com/inxaos-repo/forgekeeper.git
cd forgekeeper
```

### 2. Configure Environment

```bash
cp .env.example .env
```

Edit `.env` and set your library path:

```env
# Path to your 3D printing collection
LIBRARY_PATH=/path/to/your/3d-printing-collection

# Encryption key for stored plugin secrets
# Generate with: openssl rand -hex 32
FORGEKEEPER_ENCRYPTION_KEY=change-me-to-something-random
```

### 3. Start Services

```bash
docker compose up -d
```

Access the web UI at **http://localhost:5000**.

## Installation via Kubernetes

Forgekeeper includes Kubernetes manifests in the `k8s/` directory for production deployments with:

- CNPG PostgreSQL cluster (HA with replicas)
- NFS storage for the 3D printing collection
- Ingress with TLS via cert-manager

→ See the [Deployment Guide](deployment.md) for full Kubernetes instructions.

## First Run: What Happens

When Forgekeeper starts for the first time:

1. **Database migrations** run automatically — creates all tables and enables the `pg_trgm` extension
2. **Scanner service** starts indexing your collection — walks all source directories, reads `metadata.json` files, and creates database entries for discovered models
3. **Thumbnail worker** begins generating WebP thumbnails for STL files using `stl-thumb`

The initial scan can take several minutes for large collections (300K+ files). Progress is available via the API:

```bash
curl http://localhost:5000/api/v1/scan/status
```

## Adding Your First Source Directory

Forgekeeper expects your collection organized under a base path with source-specific subdirectories:

```
/your/3d-printing-collection/
├── sources/
│   ├── mmf/              # MyMiniFactory downloads
│   │   └── CreatorName/
│   │       └── ModelName/
│   │           ├── supported/
│   │           ├── unsupported/
│   │           ├── images/
│   │           └── metadata.json
│   ├── thangs/           # Thangs downloads
│   ├── patreon/          # Patreon drops
│   ├── cults3d/          # Cults3D downloads
│   ├── thingiverse/      # Thingiverse downloads
│   └── manual/           # Manually organized files
├── unsorted/             # Drop zone for auto-import
└── .forgekeeper/         # Thumbnails and cache (auto-created)
```

### Via the API

```bash
curl -X POST http://localhost:5000/api/v1/sources \
  -H "Content-Type: application/json" \
  -d '{
    "slug": "mmf",
    "name": "MyMiniFactory",
    "basePath": "/library/sources/mmf",
    "adapterType": "MmfSourceAdapter",
    "autoScan": true
  }'
```

### Via the UI

Navigate to the **Sources** tab in the web UI to add and manage source directories.

After adding a source, trigger a scan:

```bash
# Full scan
curl -X POST http://localhost:5000/api/v1/scan

# Incremental scan (faster — only checks changed files)
curl -X POST http://localhost:5000/api/v1/scan/incremental
```

## Using the Import Pipeline

Drop files into the `unsorted/` directory. Then trigger the import processor:

```bash
# Process the unsorted directory
curl -X POST http://localhost:5000/api/v1/import/process

# View the import queue
curl http://localhost:5000/api/v1/import/queue

# Confirm an import (sorts the file into the correct source directory)
curl -X POST http://localhost:5000/api/v1/import/queue/{id}/confirm \
  -H "Content-Type: application/json" \
  -d '{
    "creator": "SomeCreator",
    "modelName": "Cool Model",
    "sourceSlug": "manual"
  }'
```

## Navigating the UI

The Vue.js web interface provides:

- **Library view** — browse models with thumbnail grid, search, and filters
- **Model detail** — 3D STL preview (Three.js), variant list, metadata, tags, print history
- **Sources** — manage source directories and trigger scans
- **Import queue** — review and confirm auto-detected imports
- **Stats** — collection statistics and creator breakdowns

## Setting Up the MMF Plugin

The MyMiniFactory scraper plugin syncs your MMF library automatically. It requires **FlareSolverr** to bypass Cloudflare protection.

### Docker Compose

FlareSolverr is included in `docker-compose.yml` and starts automatically with `docker compose up -d`. No extra steps needed.

### Configure the Plugin

1. Open the **Plugins** tab in the web UI
2. Click **Configure** on the MMF plugin
3. Enter your MyMiniFactory username and password
4. Click **Save**
5. Click **Sync** to start your first library sync

Alternatively, via API:

```bash
# Configure credentials
curl -X PUT http://localhost:5000/api/v1/plugins/mmf/config \
  -H "Content-Type: application/json" \
  -d '{"MMF_USERNAME": "your@email.com", "MMF_PASSWORD": "your-password"}'

# Start sync
curl -X POST http://localhost:5000/api/v1/plugins/mmf/sync

# Stream progress (SSE)
curl -N http://localhost:5000/api/v1/plugins/mmf/progress

# Or poll status
curl http://localhost:5000/api/v1/plugins/mmf/status
```

### Kubernetes

Deploy FlareSolverr as a separate service (see [Deployment Guide](deployment.md#flaresolverr-required-for-mmf-plugin)). The plugin defaults to looking for FlareSolverr at:

```
http://flaresolverr.flaresolverr.svc.cluster.local:8191
```

Override via the `FLARESOLVERR_URL` config field in the Plugins UI.

## Next Steps

- [API Reference](api-reference.md) — explore all available endpoints
- [Configuration](configuration.md) — tune search, thumbnails, and security
- [Plugin Development](plugin-development.md) — build scrapers for new sources
- [Deployment](deployment.md) — Kubernetes, Flux, CNPG, NFS
