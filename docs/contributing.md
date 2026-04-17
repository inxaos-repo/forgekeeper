# Contributing Guide

## Development Setup

### Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [Node.js 22+](https://nodejs.org/) (for frontend)
- [Docker and Docker Compose](https://docs.docker.com/get-docker/) (for database)
- PostgreSQL 16 (via Docker or local install)

### Quick Start with Dev Containers

```bash
docker compose -f docker-compose.dev.yml up -d
```

This gives you:
- .NET 9 SDK container with source mounted
- PostgreSQL 16 on port 5433
- Node.js with Vite dev server on port 5173

### Manual Setup

**1. Start the Database**

```bash
docker compose up postgres -d
# Or: docker compose -f docker-compose.dev.yml up db -d
```

**2. Run Backend**

```bash
cd src/Forgekeeper.Api

# Apply migrations (auto-runs on startup, but can also run manually)
dotnet ef database update

# Run with hot-reload
dotnet watch run
```

The API starts at `http://localhost:5000`.

**3. Run Frontend**

```bash
cd src/Forgekeeper.Web
npm install
npm run dev
```

The Vite dev server starts at `http://localhost:5173` with HMR and proxies API calls to the backend.

## Project Structure

```
forgekeeper/
├── src/
│   ├── Forgekeeper.Api/              # ASP.NET Core API (entry point)
│   │   ├── Endpoints/                # Minimal API endpoint groups
│   │   ├── BackgroundServices/       # Scanner worker, thumbnail worker
│   │   ├── Middleware/               # API key auth
│   │   ├── Mcp/                      # MCP integration
│   │   └── Program.cs               # App setup and DI
│   ├── Forgekeeper.Core/            # Domain models, DTOs, interfaces, enums
│   │   ├── Models/                   # Entity classes
│   │   ├── DTOs/                     # Request/response objects
│   │   ├── Interfaces/               # Service contracts
│   │   └── Enums/                    # SourceType, VariantType, etc.
│   ├── Forgekeeper.Infrastructure/   # EF Core, repositories, services
│   │   ├── Data/                     # DbContext, migrations
│   │   ├── Repositories/             # Data access
│   │   ├── Services/                 # Business logic (scanner, search, import, etc.)
│   │   └── SourceAdapters/           # Per-source file organization adapters
│   ├── Forgekeeper.PluginSdk/       # Plugin SDK (ILibraryScraper + types)
│   └── Forgekeeper.Web/             # Vue.js 3 SPA
├── plugins/
│   └── Forgekeeper.Scraper.Mmf/     # Example plugin: MyMiniFactory
├── templates/
│   └── Forgekeeper.Scraper.Template/ # dotnet new template for plugins
├── tests/
│   ├── Forgekeeper.Tests/           # Unit + integration tests (xUnit)
│   └── Forgekeeper.E2E/             # Playwright end-to-end tests
├── k8s/                              # Kubernetes manifests
├── docker-compose.yml                # Production Docker Compose
├── docker-compose.dev.yml            # Development Docker Compose
├── Dockerfile                        # Multi-stage production build
├── Forgekeeper.sln                   # Solution file
└── init-db.sql                       # pg_trgm extension init
```

## Running Tests

### Unit & Integration Tests

```bash
# Run all tests
dotnet test

# Run with verbose output
dotnet test --verbosity normal

# Run specific test class
dotnet test --filter "FullyQualifiedName~MetadataParsingTests"

# Run specific category
dotnet test --filter "Category=Repository"
```

**Test projects:**

| Project | Tests |
|---------|-------|
| `tests/Forgekeeper.Tests` | Unit tests for parsing, adapters, services, repositories, plugins |

Key test files:
- `MetadataParsingTests.cs` — metadata.json deserialization
- `MetadataServiceTests.cs` — metadata read/write round-trips
- `SourceAdapterTests.cs` — per-source file organization
- `ImportServiceTests.cs` — import pipeline logic
- `FileScannerServiceTests.cs` — filesystem scanning
- `SearchServiceTests.cs` — search and filtering
- `RepositoryTests.cs` — data access
- `VariantDetectionTests.cs` — variant type detection
- `PluginHostServiceTests.cs` — plugin loading and management
- `MmfScraperPluginTests.cs` — MMF plugin specifics
- `ApiIntegrationTests.cs` — API endpoint tests via `WebApplicationFactory`

### End-to-End Tests (Playwright)

```bash
# Via Docker Compose (recommended — no local browser install needed)
docker compose run --rm e2e

# Or locally
cd tests/Forgekeeper.E2E
npm ci
npx playwright install
npx playwright test --reporter=list
```

E2E tests run against a live Forgekeeper instance and test the full UI + API flow.

## Code Style

### C# Conventions

- **Target:** .NET 9 / C# 13
- **Nullable reference types:** enabled
- **Implicit usings:** enabled
- Use **Minimal APIs** for endpoints (not controllers)
- Use **records** for immutable DTOs where appropriate
- Use **init-only** properties for SDK types
- Follow standard .NET naming conventions (PascalCase for public members)
- JSONB columns use `List<T>` or strongly-typed classes (not raw `JsonElement`)
- EF Core uses **snake_case naming convention** via `UseSnakeCaseNamingConvention()`

### Frontend Conventions

- **Vue 3** with Composition API (`<script setup>`)
- **TypeScript** preferred
- **Tailwind CSS** for styling
- **Vite** for build tooling

### General

- Keep endpoint files focused — one file per resource group
- DTOs live in `Forgekeeper.Core/DTOs/`
- Domain models live in `Forgekeeper.Core/Models/`
- Service interfaces in `Forgekeeper.Core/Interfaces/`, implementations in `Forgekeeper.Infrastructure/Services/`
- Plugins are separate projects referencing only `Forgekeeper.PluginSdk`

## PR Process

1. **Fork** the repository (or create a feature branch)
2. **Write tests** for new functionality
3. **Ensure all tests pass:** `dotnet test`
4. **Follow existing patterns** — look at similar endpoints/services for reference
5. **Update documentation** if adding new features or changing APIs
6. **Submit a PR** with a clear description of what and why

### PR Checklist

- [ ] Tests added/updated for the change
- [ ] `dotnet test` passes
- [ ] No new compiler warnings
- [ ] API changes documented in `docs/api-reference.md`
- [ ] Config changes documented in `docs/configuration.md`
- [ ] Breaking changes called out in PR description

## Architecture Decisions

### Why Minimal APIs (not Controllers)?

- Less boilerplate for a single-purpose API
- Better performance (no MVC middleware pipeline)
- Clean separation with endpoint group files
- Easier to understand — each endpoint file is self-contained

### Why PostgreSQL with pg_trgm?

- Trigram matching provides fuzzy search without Elasticsearch
- Works well for model names, creator names, and tag searches
- Single database dependency instead of separate search infrastructure
- Configurable similarity threshold for precision vs. recall tuning

### Why metadata.json (not database-only)?

- **Database-free recovery:** re-scanning the filesystem rebuilds everything
- **Tool-agnostic:** any external tool can write metadata.json
- **Human-readable:** users can inspect and edit metadata manually
- **Round-trip safe:** Forgekeeper preserves unknown fields via `[JsonExtensionData]`

### Why Source-Parallel Directories?

Each source (MMF, Thangs, etc.) gets its own directory tree. This means:
- Scrapers write to their own directories without conflicts
- File organization matches how files are originally downloaded
- Easy to add/remove sources without reorganizing everything

### Why JSONB for Print History, Components, etc.?

- These are model-owned data that varies per model
- No need for separate join tables for rarely-queried nested data
- EF Core 9 + Npgsql handles JSONB columns natively
- Simpler queries for the common case (load model + all its data)

### Why Plugin DLLs (not HTTP microservices)?

- Simpler deployment — just copy a DLL, no separate process
- Shared `PluginContext` provides everything the plugin needs
- No inter-process communication overhead
- Plugins can use the host's HttpClient, logger, and token store
