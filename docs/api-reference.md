# REST API Reference

Base URL: `/api/v1`

All endpoints return JSON. Timestamps are UTC ISO 8601. IDs are UUIDs (GUIDs).

Authentication is optional — if `Security:ApiKey` is configured, include it via `X-Api-Key` header. The `/health` endpoint is always unauthenticated.

---

## System

### `GET /version` — Version Info

Returns build version, .NET runtime version, and build timestamp. Always unauthenticated (not gated by API key).

**Response:**

```json
{
  "name": "Forgekeeper",
  "version": "1.0.0",
  "buildTime": "2026-04-15T18:00:00Z",
  "dotNetVersion": "9.0.4"
}
```

```bash
curl http://localhost:5000/version
```

---

## Models

### `GET /api/v1/models` — Search Models

Search and filter the model library with pagination.

**Query Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `query` | string | Full-text fuzzy search (name, creator, tags) |
| `creatorId` | UUID | Filter by creator ID |
| `creator` | string | Filter by creator name (substring match) |
| `category` | string | Filter by category (e.g., "Warhammer 40K") |
| `gameSystem` | string | Filter by game system |
| `scale` | string | Filter by scale (e.g., "28mm", "32mm") |
| `source` | string | Filter by source slug |
| `tags` | string | Comma-separated tag names (must have ALL) |
| `licenseType` | string | Filter by license type |
| `collectionName` | string | Filter by collection name |
| `printed` | bool | Filter by print status |
| `minRating` | int | Minimum rating (1-5) |
| `fileType` | enum | Filter by file type (`Stl`, `Obj`, `Threemf`, `Lys`, `Ctb`, `Gcode`, etc.) |
| `acquisitionMethod` | enum | Filter by acquisition method (`Purchase`, `Subscription`, `Free`, `Campaign`, `Gift`) |
| `publishedAfter` | datetime | Filter models published on/after this date (ISO 8601 UTC) |
| `publishedBefore` | datetime | Filter models published on/before this date (ISO 8601 UTC) |
| `sortBy` | string | Sort field: `name`, `date`, `rating`, `filecount`, `size`, `creator` (default: `name`) |
| `sortDescending` | bool | Sort descending (default: `false`) |
| `page` | int | Page number, 1-based (default: `1`) |
| `pageSize` | int | Results per page (default: `50`) |

**Response:**

```json
{
  "items": [
    {
      "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
      "name": "Dragon Miniature",
      "creatorName": "AwesomeSculptor",
      "creatorId": "...",
      "source": "Mmf",
      "sourceSlug": "mmf",
      "fileCount": 12,
      "totalSizeBytes": 45678901,
      "thumbnailPath": "/path/to/thumb.webp",
      "printed": true,
      "rating": 5,
      "tags": ["dragon", "fantasy", "28mm"],
      "createdAt": "2026-04-15T18:00:00Z",
      "updatedAt": "2026-04-15T18:00:00Z"
    }
  ],
  "totalCount": 1234,
  "page": 1,
  "pageSize": 50,
  "totalPages": 25,
  "hasNext": true,
  "hasPrevious": false
}
```

```bash
# Search for dragon models from MyMiniFactory
curl "http://localhost:5000/api/v1/models?query=dragon&source=mmf&pageSize=10"
```

---

### `GET /api/v1/models/{id}` — Get Model Detail

Returns full model detail including variants, print history, components, and related models.

**Response:**

```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "name": "Dragon Miniature",
  "creatorName": "AwesomeSculptor",
  "creatorId": "...",
  "source": "Mmf",
  "sourceSlug": "mmf",
  "sourceUrl": "https://myminifactory.com/object/123",
  "description": "An epic dragon miniature",
  "category": "Fantasy",
  "scale": "28mm",
  "gameSystem": "D&D",
  "fileCount": 12,
  "totalSizeBytes": 45678901,
  "thumbnailPath": "/path/to/thumb.webp",
  "previewImages": ["images/preview1.jpg", "images/preview2.jpg"],
  "basePath": "/library/sources/mmf/AwesomeSculptor/Dragon Miniature",
  "printed": true,
  "rating": 5,
  "notes": "Great detail, printed at 0.03mm",
  "licenseType": "personal",
  "collectionName": "Dragon Collection",
  "tags": ["dragon", "fantasy", "28mm"],
  "variants": [
    {
      "id": "...",
      "variantType": "Unsupported",
      "filePath": "unsupported/dragon_body.stl",
      "fileName": "dragon_body.stl",
      "fileType": "Stl",
      "fileSizeBytes": 12345678,
      "thumbnailPath": "/path/to/variant_thumb.webp",
      "physicalProperties": {
        "boundingBox": { "x": 50.0, "y": 40.0, "z": 80.0 },
        "unit": "mm",
        "triangleCount": 250000
      }
    }
  ],
  "printHistory": [
    {
      "id": "...",
      "date": "2026-04-10",
      "printer": "Elegoo Saturn 4 Ultra",
      "technology": "resin",
      "material": "Elegoo ABS-like Grey",
      "layerHeight": 0.03,
      "result": "success",
      "notes": "Perfect print"
    }
  ],
  "components": [
    { "name": "Body", "file": "dragon_body.stl", "required": true, "group": null },
    { "name": "Wings Option A", "file": "wings_a.stl", "required": true, "group": "wings" },
    { "name": "Wings Option B", "file": "wings_b.stl", "required": false, "group": "wings" }
  ],
  "relatedModels": [
    { "id": "...", "name": "Dragon Base", "thumbnailPath": "...", "relationType": "companion" }
  ],
  "createdAt": "2026-04-15T18:00:00Z",
  "updatedAt": "2026-04-15T18:00:00Z"
}
```

---

### `PATCH /api/v1/models/{id}` — Update Model

Update model metadata. Only provided fields are changed.

**Request Body:**

```json
{
  "name": "Updated Name",
  "category": "Warhammer 40K",
  "scale": "32mm",
  "gameSystem": "40K",
  "rating": 4,
  "notes": "Good quality sculpt"
}
```

**Response:** `200 OK`

```bash
curl -X PATCH "http://localhost:5000/api/v1/models/{id}" \
  -H "Content-Type: application/json" \
  -d '{"rating": 5, "category": "Fantasy"}'
```

---

### `DELETE /api/v1/models/{id}` — Delete Model

Delete a model from the database. Optionally deletes files from disk.

**Query Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `deleteFiles` | bool | Also delete files from disk (default: `false`) |

**Response:** `204 No Content`

```bash
curl -X DELETE "http://localhost:5000/api/v1/models/{id}?deleteFiles=true"
```

---

### `POST /api/v1/models/{id}/prints` — Add Print History Entry

Record that a model was printed.

**Request Body:**

```json
{
  "date": "2026-04-15",
  "printer": "Elegoo Saturn 4 Ultra",
  "technology": "resin",
  "material": "Elegoo ABS-like Grey",
  "layerHeight": 0.03,
  "scale": 1.0,
  "result": "success",
  "notes": "Perfect print, no failures",
  "duration": "3h 45m",
  "photos": ["photo1.jpg"],
  "variant": "presupported"
}
```

**Response:** `201 Created` with the created print history entry.

---

### `DELETE /api/v1/models/{id}/prints/{printId}` — Delete Print History Entry

Delete a specific print history entry from a model.

**Response:** `204 No Content`

---

### `PUT /api/v1/models/{id}/components` — Update Components

Replace the component list for a multi-part model.

**Request Body:**

```json
{
  "components": [
    { "name": "Body", "file": "body.stl", "required": true, "group": null },
    { "name": "Head A", "file": "head_a.stl", "required": true, "group": "head" },
    { "name": "Head B", "file": "head_b.stl", "required": false, "group": "head" }
  ]
}
```

**Response:** `200 OK` with the updated components list.

---

### `GET /api/v1/models/{id}/related` — Get Related Models

Returns all models related to this one (both directions).

**Response:**

```json
[
  { "id": "...", "name": "Companion Base", "thumbnailPath": "...", "relationType": "companion" },
  { "id": "...", "name": "Original Sculpt", "thumbnailPath": "...", "relationType": "remix" }
]
```

---

### `POST /api/v1/models/{id}/related` — Add Model Relation

Create a relationship between two models.

**Request Body:**

```json
{
  "relatedModelId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "relationType": "companion"
}
```

Relation types: `collection`, `companion`, `remix`, `alternate`, `base`.

**Response:** `201 Created` with the related model summary.  
**409 Conflict** if the relation already exists.

---

### `POST /api/v1/models/{id}/rename` — Rename/Move Model

Rename a model and optionally move its files on disk. Supports template-based naming.

**Request Body:**

```json
{
  "newName": "Dragon Miniature - AwesomeSculptor",
  "moveFiles": true,
  "template": null
}
```

**Response:** `200 OK` with the updated model.

**409 Conflict** if a model at the target path already exists.

---

### `POST /api/v1/models/rename/preview` — Template Rename Preview

Preview what a template-based rename would produce for a set of models without applying changes.

**Request Body:**

```json
{
  "modelIds": ["id1", "id2"],
  "template": "{creator} - {name}"
}
```

**Template variables:** `{name}`, `{creator}`, `{source}`, `{category}`, `{gameSystem}`, `{scale}`, `{rating}`, `{id}`

**Response:**

```json
[
  { "modelId": "id1", "currentName": "Dragon", "newName": "AwesomeSculptor - Dragon" },
  { "modelId": "id2", "currentName": "Orc Warrior", "newName": "AwesomeSculptor - Orc Warrior" }
]
```

---

### `POST /api/v1/models/bulk` — Bulk Update Models

Apply an operation to multiple models at once (max 500).

**Request Body:**

```json
{
  "modelIds": ["id1", "id2", "id3"],
  "operation": "tag",
  "value": "painted"
}
```

**Operations:** `tag`, `categorize`, `setgamesystem`, `setscale`, `setrating` (1-5), `setlicense`

**Response:**

```json
{
  "affectedCount": 3,
  "operation": "tag",
  "value": "painted"
}
```

---

### `POST /api/v1/models/bulk-tags` — Bulk Add/Remove Tags

Add and/or remove tags from multiple models at once.

**Request Body:**

```json
{
  "modelIds": ["id1", "id2"],
  "addTags": ["fantasy", "28mm"],
  "removeTags": ["unpainted"]
}
```

**Response:**

```json
{
  "affectedCount": 2,
  "tagsAdded": 4,
  "tagsRemoved": 2
}
```

---

### `POST /api/v1/models/bulk-metadata` — Bulk Multi-Field Update + Tags

Update multiple fields (and optionally tags) across many models in a single call. More powerful than `bulk` (single field) or `bulk-tags`.

**Request Body:**

```json
{
  "modelIds": ["id1", "id2"],
  "fields": {
    "category": "Warhammer 40K",
    "gameSystem": "40K",
    "scale": "32mm",
    "rating": 4,
    "printed": true
  },
  "addTags": ["warhammer", "space-marine"],
  "removeTags": ["unsorted"]
}
```

**Response:**

```json
{ "affectedCount": 2 }
```

---

### `POST /api/v1/models/bulk-creator` — Bulk Creator Reassignment

Reassign multiple models to a different creator.

**Request Body:**

```json
{
  "modelIds": ["id1", "id2", "id3"],
  "creatorId": "3fa85f64-5717-4562-b3fc-2c963f66afa6"
}
```

**Response:**

```json
{ "affectedCount": 3 }
```

---

### `GET /api/v1/models/duplicates` — Find Duplicates

Find potential duplicate models by name or file hash.

**Query Parameters:**

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `minSimilarity` | double | `0.7` | Minimum similarity threshold |
| `limit` | int | `50` | Max results (1-200) |

**Response:**

```json
[
  {
    "matchType": "name",
    "similarity": 1.0,
    "models": [
      { "id": "...", "name": "Dragon", "creatorName": "Creator A", "basePath": "...", "totalSizeBytes": 1234 },
      { "id": "...", "name": "Dragon", "creatorName": "Creator B", "basePath": "...", "totalSizeBytes": 5678 }
    ]
  }
]
```

---

## Creators

### `GET /api/v1/creators` — List Creators

**Response:**

```json
[
  {
    "id": "...",
    "name": "AwesomeSculptor",
    "source": "Mmf",
    "sourceUrl": "https://myminifactory.com/users/awesome",
    "avatarUrl": "https://...",
    "modelCount": 42,
    "createdAt": "2026-01-01T00:00:00Z"
  }
]
```

---

### `GET /api/v1/creators/{id}` — Get Creator Detail

Returns creator with full model list, total size, and file count.

**Response:**

```json
{
  "id": "...",
  "name": "AwesomeSculptor",
  "source": "Mmf",
  "modelCount": 42,
  "totalSizeBytes": 1234567890,
  "totalFileCount": 567,
  "models": [ ... ]
}
```

---

### `GET /api/v1/creators/{id}/models` — Get Creator's Models

Returns all models by a specific creator.

---

## Tags

### `GET /api/v1/tags` — List All Tags

> **Tip:** To find all models with a specific tag, use `GET /api/v1/models?tags={tagName}`.

**Response:**

```json
[
  { "id": "...", "name": "dragon", "modelCount": 15 },
  { "id": "...", "name": "terrain", "modelCount": 230 }
]
```

---

### `POST /api/v1/models/{id}/tags` — Add Tags to Model

**Request Body:**

```json
{
  "tags": ["fantasy", "dragon", "28mm"]
}
```

**Response:** `200 OK` with updated tag list.

---

### `DELETE /api/v1/models/{id}/tags/{tagName}` — Remove Tag from Model

**Response:** `204 No Content`

```bash
curl -X DELETE "http://localhost:5000/api/v1/models/{id}/tags/dragon"
```

---

## Sources

### `GET /api/v1/sources` — List Sources

**Response:**

```json
[
  {
    "id": "...",
    "slug": "mmf",
    "name": "MyMiniFactory",
    "basePath": "/library/sources/mmf",
    "adapterType": "MmfSourceAdapter",
    "autoScan": true,
    "modelCount": 500,
    "createdAt": "2026-01-01T00:00:00Z",
    "updatedAt": "2026-01-01T00:00:00Z"
  }
]
```

---

### `POST /api/v1/sources` — Create Source

**Request Body:**

```json
{
  "slug": "cults3d",
  "name": "Cults3D",
  "basePath": "/library/sources/cults3d",
  "adapterType": "GenericSourceAdapter",
  "autoScan": true
}
```

**Response:** `201 Created`  
**409 Conflict** if slug already exists.

---

### `GET /api/v1/sources/{slug}` — Get Source

Returns a single source by its slug.

**Response:**

```json
{
  "id": "...",
  "slug": "mmf",
  "name": "MyMiniFactory",
  "basePath": "/library/sources/mmf",
  "adapterType": "MmfSourceAdapter",
  "autoScan": true,
  "modelCount": 500,
  "createdAt": "2026-01-01T00:00:00Z",
  "updatedAt": "2026-01-01T00:00:00Z"
}
```

---

### `PATCH /api/v1/sources/{slug}` — Update Source

Update source settings. All fields optional (patch semantics).

**Request Body:**

```json
{
  "name": "MyMiniFactory",
  "basePath": "/library/sources/mmf",
  "adapterType": "MmfSourceAdapter",
  "autoScan": true
}
```

**Response:** `200 OK`

---

### `DELETE /api/v1/sources/{slug}` — Delete Source

**Response:** `204 No Content`

---

## Scanner

### `POST /api/v1/scan` — Start Full Scan

Triggers a full filesystem scan of all sources. Returns immediately; poll status for progress.

**Response:** `202 Accepted`

```json
{ "message": "Full scan started", "progress": { ... } }
```

**409 Conflict** if a scan is already running.

---

### `POST /api/v1/scan/incremental` — Start Incremental Scan

Scans only files that have changed since the last scan.

**Response:** `202 Accepted`

---

### `POST /api/v1/scan/verify` — Verify Library Integrity

Walks all models and variants in the database and checks that their `BasePath` directories and variant files exist on disk. Non-destructive — does not modify any records.

**Response:** `200 OK`

```json
{
  "totalModels": 5000,
  "verifiedModels": 4985,
  "missingModels": 15,
  "totalFiles": 35000,
  "verifiedFiles": 34950,
  "missingFiles": 50,
  "missingItems": [
    { "type": "model", "id": "...", "path": "/library/sources/mmf/Creator/Missing Model" },
    { "type": "file", "id": "...", "path": "/library/sources/mmf/Creator/Model/missing.stl" }
  ]
}
```

---

### `GET /api/v1/scan/untracked` — Get Untracked Files

Returns files present on disk in source directories that are not tracked in the database.

**Query Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `source` | string | Filter by source slug (optional) |

**Response:**

```json
[
  {
    "path": "/mnt/3dprinting/sources/mmf/SomeCreator/OldModel/model.stl",
    "source": "mmf",
    "sizeBytes": 1234567
  }
]
```

---

### `GET /api/v1/scan/status` — Get Scan Status

**Response:**

```json
{
  "isRunning": true,
  "status": "scanning",
  "directoriesScanned": 150,
  "modelsFound": 1200,
  "filesFound": 8500,
  "newModels": 45,
  "updatedModels": 12,
  "startedAt": "2026-04-15T18:00:00Z",
  "completedAt": null,
  "elapsedSeconds": 42.5,
  "error": null
}
```

---

## Import

### `POST /api/v1/import/scan` — Scan Arbitrary Path

Scans any provided directory path for model folders with rich auto-detection. Non-destructive preview — does not create any database records.

**Request Body:**

```json
{
  "path": "/mnt/3dprinting/unsorted",
  "maxDepth": 3,
  "recursive": true
}
```

**Response:** `200 OK` with list of detected model entries.

```json
[
  {
    "folderPath": "/mnt/3dprinting/unsorted/some_creator/dragon_model",
    "detectedModelName": "Dragon Model",
    "detectedCreatorName": "Some Creator",
    "alreadyInLibrary": false,
    "existingModelId": null,
    "files": ["dragon_body.stl", "dragon_wings.stl"],
    "hasMetadataJson": false
  }
]
```

---

### `POST /api/v1/import/process` — Process Unsorted Directory

Scans the `unsorted/` directory and creates import queue entries with auto-detected metadata.

**Response:** `200 OK` with list of detected items.

---

### `GET /api/v1/import/queue` — Get Import Queue

**Query Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `status` | string | Filter by status: `Pending`, `AutoSorted`, `AwaitingReview`, `Confirmed`, `Failed` |

**Response:**

```json
[
  {
    "id": "...",
    "originalPath": "/library/unsorted/some_model.stl",
    "detectedCreator": "SomeCreator",
    "detectedModelName": "Some Model",
    "detectedSource": "Manual",
    "confidenceScore": 0.85,
    "status": "AwaitingReview",
    "createdAt": "2026-04-15T18:00:00Z"
  }
]
```

---

### `POST /api/v1/import/queue/{id}/confirm` — Confirm Import

Confirm an import and specify the correct creator, model name, and source.

**Request Body:**

```json
{
  "creator": "ActualCreator",
  "modelName": "Actual Model Name",
  "source": "Mmf",
  "sourceSlug": "mmf"
}
```

**Response:** `200 OK`

---

### `DELETE /api/v1/import/queue/{id}` — Dismiss Import

Remove an item from the import queue without importing.

**Response:** `204 No Content`

---

### `GET /api/v1/import/watch-directories` — List Watch Directories

Returns the configured watch directories and auto-import settings.

**Response:**

```json
{
  "watchDirectories": ["/mnt/3dprinting/downloads", "/mnt/3dprinting/inbox"],
  "unsortedDirectories": ["/mnt/3dprinting/unsorted"],
  "autoImportEnabled": false,
  "intervalMinutes": 30
}
```

Configuration keys: `Import:WatchDirectories`, `Import:AutoImportEnabled`, `Import:IntervalMinutes`.

---

### `POST /api/v1/import/scan-directory` — Process Specific Directory

Triggers import processing for a specific directory path.

**Request Body:**

```json
{
  "path": "/mnt/3dprinting/downloads/new-models"
}
```

**Response:** `200 OK`

```json
{
  "processed": 12,
  "items": [
    {
      "id": "...",
      "originalPath": "/mnt/3dprinting/downloads/new-models/dragon",
      "detectedModelName": "Dragon Miniature",
      "status": "AwaitingReview"
    }
  ]
}
```

---

## Files

### `GET /api/v1/files/browse` — Server-Side File Browser

Browse the server’s filesystem for directory and file selection (used in the import flow by `FileBrowser.vue`).

**Security:** Path traversal protected — only paths within `Storage:BasePaths` plus `/mnt`, `/library`, and `/data` are accessible.

**Query Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `path` | string | Directory path to browse (e.g., `/mnt/3dprinting`) |

**Response:**

```json
{
  "currentPath": "/mnt/3dprinting",
  "entries": [
    {
      "name": "sources",
      "type": "directory",
      "size": null,
      "modified": "2026-04-01T00:00:00Z",
      "itemCount": 42
    },
    {
      "name": "unsorted",
      "type": "directory",
      "size": null,
      "modified": "2026-04-14T12:00:00Z",
      "itemCount": 8
    }
  ],
  "breadcrumbs": [
    { "name": "mnt", "path": "/mnt" },
    { "name": "3dprinting", "path": "/mnt/3dprinting" }
  ]
}
```

---

## Export / Backup

### `GET /api/v1/export` — Export Full Library

Dumps the entire library as a JSON attachment download. Includes creators, models (with tags, variants, and relations), tags, sources, recent sync runs, and non-secret plugin configs. Secrets are excluded.

**Response:** `200 OK` with `Content-Disposition: attachment; filename="forgekeeper-export-YYYYMMDD-HHmmss.json"`

```bash
curl -O -J http://localhost:5000/api/v1/export
```

---

### `POST /api/v1/import/restore` — Restore from Export

Restores a library from a previously exported JSON file. Upserts creators, tags, models (matched by `SourceId+Source` or `BasePath`), and variants. Skips encrypted secrets.

**Request Body:** JSON export file (same format as `GET /api/v1/export` output).

**Response:** `200 OK`

```json
{
  "created": 312,
  "updated": 145,
  "skipped": 23
}
```

---

## Stats

### `GET /api/v1/stats` — Collection Statistics

**Response:**

```json
{
  "totalModels": 5000,
  "totalCreators": 200,
  "totalFiles": 35000,
  "totalSizeBytes": 500000000000,
  "printedCount": 150,
  "unprintedCount": 4850,
  "modelsBySource": { "Mmf": 2000, "Thangs": 1500, "Patreon": 1000 },
  "modelsByCategory": { "Warhammer 40K": 800, "Fantasy": 600 },
  "filesByType": { "Stl": 30000, "Obj": 2000, "Threemf": 3000 },
  "topCreators": [
    { "id": "...", "name": "TopSculptor", "modelCount": 150, "totalSizeBytes": 50000000000 }
  ]
}
```

---

### `GET /api/v1/stats/creators` — Creator Statistics

Returns all creators sorted by model count with total sizes.

**Response:**

```json
[
  { "id": "...", "name": "TopSculptor", "modelCount": 150, "totalSizeBytes": 50000000000 }
]
```

---

## Plugins

### `GET /api/v1/plugins` — List Plugins

**Response:**

```json
[
  {
    "slug": "mmf",
    "name": "MyMiniFactory",
    "description": "Scrapes 3D printing model library data from MyMiniFactory (MMF).",
    "version": "1.0.0",
    "author": "Plugin Author",
    "requiresBrowserAuth": true,
    "loadedAt": "2026-04-15T18:00:00Z",
    "source": "builtin",
    "manifestValid": true,
    "manifestErrors": [],
    "manifestWarnings": [],
    "sdkCompatLevel": "Compatible",
    "sdkCompatReason": "Plugin SDK 1.0.0 matches host SDK 1.0.0",
    "syncStatus": {
      "isRunning": false,
      "lastSyncAt": "2026-04-14T12:00:00Z",
      "totalModels": 500,
      "scrapedModels": 498,
      "failedModels": 2,
      "error": null
    }
  }
]
```

**New fields:**

| Field | Type | Description |
|-------|------|-------------|
| `author` | string? | Plugin author from manifest |
| `manifestValid` | bool? | Whether `manifest.json` passed validation (`null` = no manifest) |
| `manifestErrors` | string[]? | Validation errors |
| `manifestWarnings` | string[]? | Validation warnings |
| `sdkCompatLevel` | string? | `Compatible`, `MinorMismatch`, `MajorMismatch`, `Unknown` |
| `sdkCompatReason` | string? | Human-readable explanation of SDK compatibility |
| `source` | string | Plugin origin: `builtin`, `registry`, `github`, `manual` |

---

### `GET /api/v1/plugins/{slug}/progress` — SSE Sync Progress Stream

Server-Sent Events stream that pushes real-time sync progress updates. Connect with an EventSource or `curl -N`.

```bash
curl -N http://localhost:5000/api/v1/plugins/mmf/progress
```

The stream uses **named SSE events**:

**`event: progress`** — emitted during sync:

```
event: progress
data: {"status":"running","scraped":247,"total":500,"failed":3,"currentItem":"Dragon Bust by SculptorX"}
```

**`event: complete`** — emitted when sync finishes:

```
event: complete
data: {"scraped":498,"total":500,"failed":2}
```

| Field | Description |
|-------|-------------|
| `status` | `running` during sync; set by the sync wrapper |
| `scraped` | Number of models successfully scraped so far |
| `total` | Total models expected in this sync |
| `failed` | Number of models that failed to scrape |
| `currentItem` | Display name of the item currently being processed |

Status values from plugin: `authenticating`, `fetching_manifest`, `running`, `complete`, `error`

---

### `GET /api/v1/plugins/{slug}/config` — Get Plugin Config

Returns the plugin's configuration schema with current values (secrets are masked).

**Response:**

```json
[
  {
    "key": "CLIENT_ID",
    "label": "Client ID",
    "type": "string",
    "required": true,
    "helpText": "Your MMF OAuth client ID",
    "value": "abc123",
    "isSet": true
  },
  {
    "key": "CLIENT_SECRET",
    "label": "Client Secret",
    "type": "secret",
    "required": true,
    "value": "••••••••",
    "isSet": true
  }
]
```

---

### `PUT /api/v1/plugins/{slug}/config` — Update Plugin Config

**Request Body:** (key-value dictionary)

```json
{
  "CLIENT_ID": "new-client-id",
  "CLIENT_SECRET": "new-secret"
}
```

**Response:** `200 OK`

---

### `GET /api/v1/plugins/{slug}/auth` — Get Plugin Auth Status / Trigger Auth Flow

Returns the current authentication status for the plugin, or triggers a new auth flow if needed.

**Query Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `force` | bool | Clear stored token and force a fresh authentication flow (default: `false`) |

**Response:** `200 OK` with auth status or redirect URL for browser-based auth.

---

### `POST /api/v1/plugins/{slug}/sync` — Trigger Plugin Sync

Start a sync operation for the specified plugin.

**Query Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `resume` | bool | Resume the last incomplete sync from its saved index (default: `false`) |

**Response:** `202 Accepted`

```json
{ "message": "Sync started for 'mmf'" }
```

**409 Conflict** if sync is already running.

---

### `GET /api/v1/plugins/{slug}/status` — Get Plugin Sync Status

**Response:**

```json
{
  "isRunning": true,
  "lastSyncAt": "2026-04-14T12:00:00Z",
  "totalModels": 500,
  "scrapedModels": 250,
  "failedModels": 1,
  "error": null,
  "currentProgress": {
    "status": "running",
    "scraped": 250,
    "total": 500,
    "failed": 1,
    "currentItem": "Dragon Bust by SculptorX"
  }
}
```

---

### `POST /api/v1/plugins/{slug}/manifest` — Upload Plugin Manifest

Upload a manifest file for plugins that require it (e.g., exported library data).

Accepts both `multipart/form-data` (field name: `manifest`) and raw JSON body.

**Response:**

```json
{
  "message": "Manifest loaded: 150 models found",
  "modelCount": 150
}
```

---

### `GET /api/v1/plugins/history` — All Sync Run History

Returns sync run history across all plugins, ordered by `startedAt` descending.

**Query Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `limit` | int | Max records to return (default: `50`) |

**Response:** `200 OK` with list of `SyncRun` records.

---

### `GET /api/v1/plugins/{slug}/history` — Per-Plugin Sync History

Returns sync run history for a specific plugin.

**Query Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `limit` | int | Max records to return (default: `20`) |

**Response:** `200 OK` with list of `SyncRun` records for the given slug.

---

### `POST /api/v1/plugins/reload` — Hot-Reload All Plugins

Unloads and reloads all plugins without restarting the service. Requires `Forgekeeper__HotReloadEnabled=true`; returns `501 Not Implemented` if disabled (the default).

**Response:** `200 OK` with reload summary, or `501 Not Implemented` if hot-reload is disabled.

---

### `POST /api/v1/plugins/{slug}/reload` — Hot-Reload Single Plugin

Unloads and reloads a single plugin. Same `HotReloadEnabled` gate as the bulk reload.

**Response:** `200 OK` with reload result, or `501 Not Implemented` if hot-reload is disabled.

---

### `GET /api/v1/plugins/{slug}/diagnostics` — Plugin Diagnostics

Returns full diagnostic information for a loaded plugin.

**Response:**

```json
{
  "slug": "mmf",
  "loadedAt": "2026-04-15T18:00:00Z",
  "source": "builtin",
  "sourceDirectory": "/app/plugins/mmf",
  "assemblyName": "Forgekeeper.Scraper.Mmf",
  "dllPath": "/app/plugins/mmf/Forgekeeper.Scraper.Mmf.dll",
  "manifest": { ... },
  "validation": {
    "isValid": true,
    "errors": [],
    "warnings": []
  },
  "sdkCompat": {
    "level": "Compatible",
    "reason": "Plugin SDK 1.0.0 matches host SDK 1.0.0"
  }
}
```

**404 Not Found** if slug is not recognized.

---

## Variants

### `GET /api/v1/variants/{id}/download` — Download Variant File

Returns the file directly with appropriate content type (`model/stl`, `model/obj`, `model/3mf`, etc.).

### `GET /api/v1/variants/{id}/thumbnail` — Get Variant Thumbnail

Returns the WebP thumbnail image for a specific variant.

---

## Prometheus Metrics

### `GET /metrics` — Prometheus Metrics

Returns metrics in Prometheus text format. Useful for Grafana dashboards and alerting.

Metrics include:

**Library**

| Metric | Description |
|--------|-------------|
| `forgekeeper_models_total{source="mmf"}` | Total model count per source. **Note:** currently only `mmf` and `manual` sources are exported; other sources (Thangs, Cults3D, Patreon) are not included. |
| `forgekeeper_creators_total` | Total creator count |
| `forgekeeper_files_total` | Total variant file count |
| `forgekeeper_thumbnails_total` | Total generated thumbnails |
| `forgekeeper_library_size_bytes` | Total library size in bytes |
| `forgekeeper_printed_total` | Models with at least one successful print |

**Scanner / Sync**

| Metric | Description |
|--------|-------------|
| `forgekeeper_sync_running{plugin="mmf"}` | `1` if a sync is currently active for the given plugin (replaces the old `forgekeeper_scan_running`) |
| `forgekeeper_sync_scraped_total{plugin="mmf"}` | Models successfully scraped in current/last sync |
| `forgekeeper_sync_failed_total{plugin="mmf"}` | Models that failed in current/last sync |
| `forgekeeper_sync_total_models{plugin="mmf"}` | Total models in current sync manifest |

**Plugins**

| Metric | Description |
|--------|-------------|
| `forgekeeper_plugins_loaded{slug="mmf"}` | `1` if the plugin is loaded, `0` otherwise |
| `forgekeeper_plugins_manifest_valid{slug="mmf"}` | `1` = valid manifest, `0` = invalid, `-1` = no manifest |
| `forgekeeper_plugins_sdk_compatible{slug="mmf"}` | `1` = compatible, `0` = incompatible, `-1` = unknown |

```bash
curl http://localhost:5000/metrics
```

---

## MCP (Model Context Protocol)

### `GET /mcp/tools` — List MCP Tools

Returns all available MCP tool definitions organized into read, write, and analysis categories.

**Response:**

```json
{
  "tools": [
    {
      "name": "search",
      "description": "Search 3D models by text query, filters...",
      "category": "read",
      "inputSchema": { "type": "object", "properties": { ... } }
    }
  ]
}
```

**Available tools:**

| Category | Tool | Description |
|----------|------|-------------|
| read | `search` | Search models with filters and pagination |
| read | `getModel` | Get full model details by ID |
| read | `getCreator` | Get creator details by ID |
| read | `listSources` | List configured sources |
| read | `stats` | Collection statistics |
| read | `findDuplicates` | Find potential duplicates |
| read | `findUntagged` | Find models without tags |
| read | `recent` | Get recently added models |
| write | `tagModel` | Add a tag to a model |
| write | `updateModel` | Update model metadata |
| write | `markPrinted` | Record a print |
| write | `setComponents` | Set model components |
| write | `linkModels` | Create model relationships |
| write | `bulkUpdate` | Bulk update operations |
| write | `triggerSync` | Trigger plugin sync |
| analysis | `collectionReport` | Comprehensive collection report |
| analysis | `healthCheck` | Collection health check |
| analysis | `printHistory` | Print history summary |

---

### `POST /mcp/invoke` — Invoke MCP Tool

**Request Body:**

```json
{
  "tool": "search",
  "arguments": {
    "query": "dragon",
    "category": "Fantasy",
    "pageSize": 10
  }
}
```

**Response:**

```json
{
  "content": [{ "type": "text", "text": "{ ... JSON result ... }" }],
  "isError": false
}
```

---

## Health

### `GET /health` — Health Check

Always unauthenticated. Used by Docker and Kubernetes health probes.

**Response:**

```json
{
  "status": "healthy",
  "timestamp": "2026-04-15T18:00:00Z"
}
```
