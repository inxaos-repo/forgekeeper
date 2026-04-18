# Migrating from MiniDownloader to Forgekeeper

> ⚠️ **MiniDownloader is deprecated.** Forgekeeper's built-in MMF plugin replaces it entirely — no migration of downloaded files is needed.

## Overview

Forgekeeper's MMF scraper plugin provides everything MiniDownloader did, plus a full web UI, database tracking, plugin system, and more. If you already have a MiniDownloader library, migrating is straightforward: your existing files are already in the right format.

---

## Feature Comparison

| Feature | MiniDownloader | Forgekeeper MMF Plugin |
|---------|---------------|------------------------|
| OAuth 2.0 authentication | ✅ Browser popup required | ✅ Fully headless (cookie auth via FlareSolverr) |
| Cloudflare bypass | ✅ Playwright browser | ✅ FlareSolverr (included in docker-compose.yml) |
| Library manifest fetching | ✅ Headless Chromium | ✅ Browser console paste or headless fetch |
| Smart sync (skip existing) | ✅ Timestamp comparison | ✅ Timestamp comparison + file hash dedup |
| Background ZIP extraction | ✅ | ✅ |
| Rate limiting | ✅ 1.5s between downloads | ✅ Configurable (default 3s) |
| `metadata.json` sidecar files | ✅ Writes per model | ✅ Writes + enriches with user metadata |
| Web UI for status | ✅ Simple status page | ✅ Full browsing, search, 3D preview |
| Database tracking | ❌ File-based only | ✅ PostgreSQL with pg_trgm fuzzy search |
| Variant grouping (supported/unsupported) | ❌ | ✅ Automatic detection |
| Thumbnail generation | ❌ | ✅ WebP via stl-thumb |
| Print history tracking | ❌ | ✅ Per-model print log |
| Tagging, rating, categorization | ❌ | ✅ |
| Multi-source library (MMF + Thangs + Patreon + …) | ❌ MMF only | ✅ Plugin system for any source |
| MCP interface for AI assistants | ❌ | ✅ 18 tools |
| Kubernetes-ready | ✅ | ✅ Helm chart + Flux GitOps |
| No browser popup needed | ❌ First run requires browser | ✅ Fully headless |

---

## Migration Steps

### 1. Deploy Forgekeeper

Follow the [Getting Started guide](getting-started.md) to deploy Forgekeeper via Docker Compose or Kubernetes.

```bash
git clone https://github.com/your-org/forgekeeper.git
cd forgekeeper
cp .env.example .env
# Edit .env: set LIBRARY_PATH to your existing MMF download directory
docker compose up -d
```

### 2. Point it at your existing MMF download directory

Set `LIBRARY_PATH` in your `.env` (Docker Compose) or `Storage__BasePaths__0` in your config (Kubernetes) to the **parent** of your existing library. Forgekeeper expects this structure:

```
/your/library/
└── sources/
    └── mmf/
        └── CreatorName/
            └── ModelName/
                ├── supported/
                ├── unsupported/
                └── metadata.json   ← written by MiniDownloader
```

If your existing MiniDownloader files are already in a `CreatorName/ModelName/` structure, Forgekeeper's `MmfSourceAdapter` will read them directly. No moving or renaming required.

### 3. Configure the MMF plugin

1. Open Forgekeeper at `http://localhost:5000`
2. Go to **Plugins** tab
3. Find the **MMF Scraper** plugin
4. Enter your MyMiniFactory **username** and **password**
5. Save — FlareSolverr handles the Cloudflare bypass automatically (no browser popup needed)

### 4. Run a sync

Click **Sync Now** on the MMF plugin card. Forgekeeper will:

- Fetch your current library manifest from MyMiniFactory
- Compare against your existing files
- **Skip models that already exist** — dedup by directory and file timestamps
- Download only new or updated models
- Update `metadata.json` sidecar files with enriched metadata

### 5. Your metadata.json files are preserved and enhanced

MiniDownloader's `metadata.json` files are the integration contract. Forgekeeper reads them, preserves all scraper-owned fields, and adds its own metadata (tags, ratings, print history, components) in a non-destructive merge.

**You don't lose anything.** Forgekeeper augments what MiniDownloader wrote.

---

## Notes

### FlareSolverr is included

`docker-compose.yml` includes FlareSolverr as a service — it starts automatically. No manual setup needed. The MMF plugin detects it at `http://flaresolverr:8191` via Docker networking.

### No browser popup

MiniDownloader required a browser window for the initial OAuth login. Forgekeeper's MMF plugin uses cookie-based auth via FlareSolverr — fully headless, no browser required. Just enter your username and password in the plugin config.

### Existing metadata.json files

MiniDownloader wrote `metadata.json` files alongside downloaded models. Forgekeeper reads these on the first scan and:

- Imports all source metadata (creator, dates, acquisition method, file list)
- Preserves the `extra` field verbatim
- Adds its own fields (tags, ratings, components, print history) without overwriting scraper data

### Library format compatibility

MiniDownloader's output format **is** Forgekeeper's canonical `sources/mmf/` format. The `MmfSourceAdapter` was designed around MiniDownloader's directory structure. You do not need to reorganize anything.

---

## Stopping MiniDownloader

Once Forgekeeper is handling your MMF library:

1. Stop the MiniDownloader container (if running as a service)
2. Your files and `metadata.json` sidecars are intact — Forgekeeper takes over from here

The MiniDownloader repository is archived. No further updates will be made.
