# Configuration Reference

Forgekeeper is configured via environment variables, `appsettings.json`, or a combination of both. Environment variables take precedence and use the `__` (double underscore) separator for nested values.

## Environment Variables

### Required

| Variable | Description | Example |
|----------|-------------|---------|
| `ConnectionStrings__ForgeDb` | PostgreSQL connection string | `Host=postgres;Port=5432;Database=forgekeeper;Username=forgekeeper;Password=forgekeeper` |
| `Storage__BasePaths__0` | Root path to your 3D printing collection | `/mnt/3dprinting` |

### Recommended

| Variable | Description | Default |
|----------|-------------|---------|
| `FORGEKEEPER_ENCRYPTION_KEY` | Key for encrypting stored plugin secrets. Generate with `openssl rand -hex 32` | (none — secrets stored unencrypted) |
| `Security__ApiKey` | API key for authenticating requests (via `X-Api-Key` header). If unset, all requests are allowed. | (none — open access) |

### Storage

| Variable | Description | Default |
|----------|-------------|---------|
| `Storage__BasePaths__0` | Primary library path | (required) |
| `Storage__ThumbnailDir` | Thumbnail directory name (relative to base path) | `.thumbnails` |
| `Forgekeeper__PluginsDirectory` | Path to scraper plugin DLLs | `/app/plugins` |
| `Forgekeeper__SourcesDirectory` | Path to sources directory | `{BasePath}/sources` |

### Thumbnails

| Variable | Description | Default |
|----------|-------------|---------|
| `Thumbnails__Enabled` | Enable/disable thumbnail generation | `true` |
| `Thumbnails__Renderer` | Thumbnail renderer (`stl-thumb`) | `stl-thumb` |
| `Thumbnails__Size` | Thumbnail dimensions (pixels or `WxH`) | `256` |
| `Thumbnails__Format` | Output format | `webp` |

### Search

| Variable | Description | Default |
|----------|-------------|---------|
| `Search__MinTrigramSimilarity` | Minimum pg_trgm similarity score for fuzzy search (0.0-1.0). Lower = more results, less precise. | `0.3` |

### Import

| Variable | Description | Default |
|----------|-------------|------|
| `Import__WatchDirectories__0` | First directory to auto-scan for imports (repeat for more) | (none) |
| `Import__AutoImportEnabled` | Enable automatic import processing on a schedule | `false` |
| `Import__IntervalMinutes` | How often to check watch directories (minutes) | `30` |

### Plugins

| Variable | Description | Default |
|----------|-------------|------|
| `Forgekeeper__HotReloadEnabled` | Enable hot-reload of plugins without service restart | `false` |

### ASP.NET Core

| Variable | Description | Default |
|----------|-------------|---------|
| `ASPNETCORE_ENVIRONMENT` | Runtime environment (`Development`, `Production`) | `Production` |
| `ASPNETCORE_URLS` | Listen URLs | `http://+:5000` |

### Logging (Serilog)

| Variable | Description | Default |
|----------|-------------|---------|
| `Serilog__MinimumLevel__Default` | Default log level | `Information` |
| `Serilog__MinimumLevel__Override__Microsoft` | Microsoft framework log level | `Warning` |
| `Serilog__MinimumLevel__Override__Microsoft.EntityFrameworkCore` | EF Core log level | `Warning` |

## appsettings.json Structure

```json
{
  "ConnectionStrings": {
    "ForgeDb": "Host=localhost;Port=5432;Database=forgekeeper;Username=forgekeeper;Password=forgekeeper"
  },
  "Storage": {
    "BasePaths": ["/mnt/3dprinting"],
    "ThumbnailDir": ".thumbnails"
  },
  "Forgekeeper": {
    "PluginsDirectory": "/app/plugins",
    "SourcesDirectory": "/mnt/3dprinting/sources"
  },
  "Thumbnails": {
    "Enabled": true,
    "Renderer": "stl-thumb",
    "Size": "256",
    "Format": "webp"
  },
  "Search": {
    "MinTrigramSimilarity": 0.3
  },
  "Import": {
    "WatchDirectories": [],
    "AutoImportEnabled": false,
    "IntervalMinutes": 30
  },
  "Plugins": {
    "HotReloadEnabled": false
  },
  "Security": {
    "ApiKey": null
  },
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft": "Warning",
        "Microsoft.EntityFrameworkCore": "Warning"
      }
    },
    "WriteTo": [
      { "Name": "Console" }
    ]
  }
}
```

## Docker Compose Configuration

### .env File

```env
# Path to your 3D printing collection
LIBRARY_PATH=/path/to/your/3d-printing-collection

# Encryption key for stored secrets
FORGEKEEPER_ENCRYPTION_KEY=change-me-to-something-random
```

### docker-compose.yml (Environment Section)

```yaml
services:
  forgekeeper:
    environment:
      - ConnectionStrings__ForgeDb=Host=postgres;Port=5432;Database=forgekeeper;Username=forgekeeper;Password=forgekeeper
      - Storage__BasePaths__0=/library
      - FORGEKEEPER_ENCRYPTION_KEY=change-me-to-something-random
      - ASPNETCORE_ENVIRONMENT=Production
      - ASPNETCORE_URLS=http://+:5000
      - Thumbnails__Size=256
      - Search__MinTrigramSimilarity=0.3
    volumes:
      - ${LIBRARY_PATH:-./sample-library}:/library
      - ./plugins:/app/plugins
      - forgekeeper-data:/data
```

## Kubernetes Configuration

### ConfigMap

```yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: forgekeeper-config
  namespace: forgekeeper
data:
  ConnectionStrings__ForgeDb: "Host=forgekeeper-db-rw;Port=5432;Database=forgekeeper;Username=forgekeeper;Password=forgekeeper"
  Storage__BasePaths__0: "/mnt/3dprinting"
  Storage__ThumbnailDir: ".thumbnails"
  Forgekeeper__PluginsDirectory: "/app/plugins"
  Forgekeeper__SourcesDirectory: "/mnt/3dprinting/sources"
  Thumbnails__Enabled: "true"
  Thumbnails__Renderer: "stl-thumb"
  Thumbnails__Size: "256x256"
  Thumbnails__Format: "webp"
  Search__MinTrigramSimilarity: "0.3"
  ASPNETCORE_ENVIRONMENT: "Production"
  ASPNETCORE_URLS: "http://+:5000"
  Serilog__MinimumLevel__Default: "Information"
```

### Secrets

Store sensitive values in Kubernetes Secrets (or use sealed-secrets / external-secrets):

```yaml
apiVersion: v1
kind: Secret
metadata:
  name: forgekeeper-secrets
  namespace: forgekeeper
type: Opaque
stringData:
  FORGEKEEPER_ENCRYPTION_KEY: "your-generated-key-here"
  Security__ApiKey: "your-api-key-here"
```

## Security: API Key Authentication

When `Security:ApiKey` is set, all API requests (except `/health` and `/swagger`) require the key in the `X-Api-Key` header:

```bash
# With API key configured
curl -H "X-Api-Key: your-api-key" http://localhost:5000/api/v1/models

# Without API key configured — all requests are allowed
curl http://localhost:5000/api/v1/models
```

The middleware (`ApiKeyMiddleware`) checks for the `X-Api-Key` header on every request. If no API key is configured in settings, all requests pass through (backward compatibility).

### Best Practices

- Always set an API key in production
- Use `FORGEKEEPER_ENCRYPTION_KEY` to protect stored plugin credentials
- In Kubernetes, use Secrets (not ConfigMaps) for sensitive values
- Generate keys with `openssl rand -hex 32`
