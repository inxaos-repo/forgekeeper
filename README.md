# Forgekeeper — 3D Print File Manager

A self-hosted digital asset manager for massive 3D printing collections, built for hobbyists who accumulate files from multiple sources and need to actually find things.

## Features

- **Unified library** across multiple sources (MyMiniFactory, Thangs, Patreon, Cults3D, Thingiverse, manual)
- **Smart variant handling** — supported/unsupported/presupported versions grouped under one model
- **Source-parallel directories** — each source maintains its own folder structure, Forgekeeper provides the unified view
- **Import pipeline** — drop files in `unsorted/`, auto-detect what they are, confirm and sort
- **Full-text search** with PostgreSQL pg_trgm fuzzy matching
- **3D STL preview** in the browser (Three.js)
- **Thumbnail generation** for visual browsing
- **Tagging, rating, categorization** — game system, scale, printed status, notes
- **Built for scale** — handles 300K+ files without breaking a sweat

## Architecture

```
┌─────────────────────────────────────────────┐
│ External Tools (scraper plugins)    │
│ Write files + metadata.json to sources/     │
└──────────────────┬──────────────────────────┘
                   │ metadata.json contract
┌──────────────────▼──────────────────────────┐
│ Forgekeeper                                  │
│  ├── Scanner Service (indexes files → DB)   │
│  ├── Import Service (unsorted → sorted)     │
│  ├── Search Service (pg_trgm full-text)     │
│  ├── Thumbnail Service (STL → WebP)         │
│  ├── REST API (ASP.NET Core 9 Minimal APIs) │
│  └── Vue.js 3 SPA (Three.js STL viewer)    │
├──────────────────────────────────────────────┤
│ PostgreSQL 16 (metadata, tags, search)       │
│ NFS Storage (files, thumbnails)              │
└──────────────────────────────────────────────┘
```

## Tech Stack

- **Backend:** C# / ASP.NET Core 9 (Minimal APIs)
- **Database:** PostgreSQL 16 with pg_trgm extension
- **ORM:** Entity Framework Core 9 (Npgsql)
- **Frontend:** Vue.js 3 (Composition API) + Vite + Tailwind CSS
- **3D Preview:** Three.js with STLLoader
- **Thumbnails:** stl-thumb (Rust CLI)
- **Container:** Docker (multi-stage .NET 9 build)

## Quick Start

### Prerequisites
- Docker and Docker Compose
- Your 3D printing file collection on a local path

### Run with Docker Compose

```bash
# Clone the repo
git clone https://github.com/forgekeeper/forgekeeper.git
cd forgekeeper

# Point to your collection (edit docker-compose.yml volumes section)
# Default mounts ./sample-data to /mnt/3dprinting

# Start
docker compose up -d

# Access
# API: http://localhost:8080
# Frontend: http://localhost:5173 (dev) or served by API (production)
```

### Run for Development

**Backend:**
```bash
cd src/Forgekeeper.Api
dotnet run
```

**Frontend:**
```bash
cd src/Forgekeeper.Web
npm install
npm run dev
```

**Database:**
```bash
# Start just PostgreSQL
docker compose up postgres -d

# Run migrations
cd src/Forgekeeper.Api
dotnet ef database update
```

## Directory Structure

Forgekeeper expects your collection organized under a base path:

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
│   ├── thangs/           # Thangs downloads
│   ├── patreon/          # Patreon drops
│   ├── cults3d/          # Cults3D downloads
│   ├── thingiverse/      # Thingiverse downloads
│   └── manual/           # Manually organized
├── unsorted/             # Drop zone for auto-import
└── .forgekeeper/         # Thumbnails and cache
```

## Integration Contract: metadata.json

Any external tool that writes a `metadata.json` file alongside downloaded models enables rich import:

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
  "dates": {
    "downloaded": "2026-04-15T18:00:00Z"
  },
  "files": [
    {
      "filename": "model.stl",
      "localPath": "unsupported/model.stl",
      "variant": "unsupported"
    }
  ]
}
```

See the [full spec](SPEC.md) for all fields.

### Forgekeeper Writes to metadata.json

Forgekeeper is not read-only on metadata.json. It writes back user-owned fields:

- **User-owned fields:** `tags` (additions), `license`, `collection`, `printSettings`, `printHistory`, `components`, `relatedModels`, `physicalProperties`
- **Scraper plugins preserves these on re-sync** — its `PreserveUserEdits` step round-trips all Forgekeeper-owned fields without interpreting them (stored as opaque `JsonElement`)
- **Forgekeeper MUST NOT write:** `source`, `externalId`, `creator`, `dates.created/updated/published`, `dates.downloaded`, `files[]`, `acquisition`

This enables database-free recovery — re-scanning the filesystem rebuilds the full library including user edits. Tags are merged as a union (both apps add, neither removes). License uses "user wins" policy: a user-set license takes precedence over the source value.

## API

Base URL: `/api/v1`

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/models` | List/search models |
| GET | `/models/{id}` | Model detail + variants |
| PATCH | `/models/{id}` | Update metadata |
| GET | `/creators` | List creators |
| GET | `/creators/{id}/models` | Creator's models |
| GET | `/variants/{id}/download` | Download file |
| POST | `/scan` | Trigger full scan |
| POST | `/scan/incremental` | Incremental scan |
| GET | `/import/pending` | Import queue |
| POST | `/import/confirm` | Confirm import |
| GET | `/stats` | Collection stats |
| GET | `/tags` | List tags |

## Configuration

See `appsettings.json` for all options:
- Database connection
- Storage base paths
- Scanner file types and source adapters
- Thumbnail settings
- Search parameters

## License

MIT

## Contributing

Pull requests welcome. See [SPEC.md](SPEC.md) for the full architecture documentation.
