# Forgekeeper — Plugin Lifecycle & Distribution Plan

> **Status:** Planning  
> **Version:** 1.0 draft  
> **Author:** Forgekeeper Contributors  
> **Last Updated:** 2026-04-18

---

## Overview

Forgekeeper's plugin system allows third-party developers to add scrapers for new library sources without modifying the core application. This document defines how plugins are packaged, distributed, discovered, installed, updated, and secured — from the first MMF scraper to a future community ecosystem.

The design draws lessons from:
- **Grafana** — plugin catalog + CLI install, version-pinned downloads
- **Home Assistant / HACS** — community store layered on top of GitHub, PR-based registry
- **Obsidian** — community plugin list in a GitHub repo, one-click install from settings
- **VS Code** — marketplace with publisher verification, extension host isolation
- **WordPress** — plugin directory, auto-update with compatibility checks

---

## 1. Plugin Package Format

### 1.1 Package Contents

A Forgekeeper plugin is a directory (or zip archive) with the following structure:

```
Forgekeeper.Scraper.Example/
├── Forgekeeper.Scraper.Example.dll      # Required: compiled .NET class library
├── manifest.json                         # Required: plugin metadata
├── README.md                             # Recommended: user-facing docs
├── icon.png                              # Optional: 128x128 PNG icon (shown in UI)
└── <dependency>.dll                      # Optional: bundled dependencies
```

> **Convention:** The directory name and DLL name should match the NuGet-style package ID: `Forgekeeper.Scraper.<SourceName>`.

### 1.2 manifest.json Schema

Every plugin must include a `manifest.json` at its root. See `plugins/manifest.schema.json` for the full JSON Schema. Key fields:

```json
{
  "slug": "mmf",
  "name": "My Manga Forum Scraper",
  "version": "1.0.0",
  "sdk_version": "1.0.0",
  "min_sdk_version": "1.0.0",
  "max_sdk_version": "1.x",
  "min_forgekeeper_version": "1.0.0",
  "author": "Plugin Author",
  "email": "author@example.com",
  "description": "Scrapes library data from My Manga Forum (MMF).",
  "homepage": "https://github.com/your-org/Forgekeeper.Scraper.Mmf",
  "source_url": "https://github.com/your-org/Forgekeeper.Scraper.Mmf",
  "license": "MIT",
  "tags": ["manga", "scraper", "mmf"],
  "entry_assembly": "Forgekeeper.Scraper.Mmf.dll"
}
```

| Field | Required | Description |
|---|---|---|
| `slug` | ✅ | Short unique ID (lowercase, hyphens). Used in CLI commands. |
| `name` | ✅ | Human-readable display name |
| `version` | ✅ | Plugin version (SemVer) |
| `sdk_version` | ✅ | SDK version this plugin was built against |
| `min_sdk_version` | ✅ | Minimum SDK version required |
| `max_sdk_version` | ✅ | Maximum compatible SDK version (`1.x` = any 1.y) |
| `min_forgekeeper_version` | ✅ | Minimum Forgekeeper app version |
| `author` | ✅ | Author display name |
| `description` | ✅ | Short description (1-2 sentences) |
| `entry_assembly` | ✅ | DLL filename to load |
| `email` | ○ | Author contact |
| `homepage` | ○ | Project homepage URL |
| `source_url` | ○ | Source code URL |
| `license` | ○ | SPDX license identifier |
| `tags` | ○ | Array of category tags |

### 1.3 Versioning

**Plugins** use [Semantic Versioning](https://semver.org/):
- `MAJOR` — breaking changes to scraper behavior or manifest format
- `MINOR` — new features, new sources, backward-compatible
- `PATCH` — bug fixes, scraping fixes for site changes

**SDK** (`Forgekeeper.PluginSdk`) also uses SemVer with a strict compatibility contract:
- `MINOR` bumps — new interfaces/methods added, all existing plugins still work
- `MAJOR` bumps — breaking changes to `ILibraryScraper` or core types

**SDK Compatibility Matrix:**

| SDK Major | Compatible Plugin `sdk_version` | Notes |
|---|---|---|
| 1.x | 1.0.0 – 1.x | All 1.x plugins work with any 1.y host |
| 2.x | 2.0.0 – 2.x | 1.x plugins will NOT load (breaking change) |

Forgekeeper refuses to load a plugin if:
- `sdk_version` major > host SDK major (plugin too new)
- `sdk_version` major < host SDK major (plugin too old, may re-enable with `--force`)

---

## 2. Distribution Channels

### 2.1 Built-in Plugins (Ship with Docker Image)

First-party scrapers (MMF and any officially maintained ones) are bundled directly into the Docker image at `/app/plugins/`. They require no install step — they just work out of the box.

- Updated by upgrading the Forgekeeper Docker image
- No manifest.json required (but strongly recommended for consistency)
- Cannot be uninstalled via CLI (they're baked in), but can be disabled in config

### 2.2 GitHub Releases (Per-Plugin Repos)

Each community plugin lives in its own GitHub repo (e.g., `github.com/author/Forgekeeper.Scraper.SomeSite`).

Releases follow this convention:
- Tag: `v1.2.3`
- Release assets:
  - `Forgekeeper.Scraper.SomeSite-1.2.3.zip` — full plugin package (DLL + manifest + deps)
  - `manifest.json` — also attached separately for quick metadata fetching
  - `SHA256SUMS` — checksums for verification

This pattern allows Forgekeeper to install a plugin from any GitHub URL without a central registry:
```bash
forgekeeper plugin install https://github.com/author/Forgekeeper.Scraper.SomeSite/releases/latest
```

### 2.3 Plugin Registry (Central Catalog)

A central plugin registry hosted in the `forgekeeper/plugin-registry` GitHub repo provides discoverability. The registry is a simple JSON file served via GitHub Pages or raw.githubusercontent.com:

```
https://raw.githubusercontent.com/forgekeeper/plugin-registry/main/registry.json
```

**`registry.json` structure:**

```json
{
  "version": "1",
  "updated": "2026-04-18T00:00:00Z",
  "plugins": [
    {
      "slug": "mmf",
      "name": "My Manga Forum Scraper",
      "version": "1.0.0",
      "author": "Damon Prater",
      "description": "Scrapes library data from My Manga Forum (MMF).",
      "homepage": "https://github.com/inxaos/Forgekeeper.Scraper.Mmf",
      "download_url": "https://github.com/inxaos/Forgekeeper.Scraper.Mmf/releases/download/v1.0.0/Forgekeeper.Scraper.Mmf-1.0.0.zip",
      "sdk_version": "1.0.0",
      "min_sdk_version": "1.0.0",
      "checksum_sha256": "abc123...",
      "tags": ["manga", "scraper"],
      "updated": "2026-04-18T00:00:00Z"
    }
  ]
}
```

**Registry management:**
- Community plugins submitted via Pull Request to `forgekeeper/plugin-registry`
- Maintainers review for basic sanity (valid manifest, working download URL, no obvious malware)
- Merged PRs automatically update the hosted registry.json
- Forgekeeper caches registry.json locally for 24 hours

### 2.4 Manual Install

Power users and developers can install plugins by hand:

1. Build or download the plugin zip
2. Extract to the plugins directory: `/data/plugins/<slug>/` (or wherever `plugins.path` points)
3. Ensure `manifest.json` is present at the root of the plugin directory
4. Restart Forgekeeper, or trigger hot-reload via `POST /api/plugins/reload`

**Plugins directory layout:**
```
/data/plugins/
├── mmf/
│   ├── Forgekeeper.Scraper.Mmf.dll
│   └── manifest.json
└── some-other-site/
    ├── Forgekeeper.Scraper.SomeOtherSite.dll
    └── manifest.json
```

---

## 3. Installation Flow

### 3.1 CLI Install

```bash
# Install from registry by slug
forgekeeper plugin install mmf

# Install from GitHub URL (latest release)
forgekeeper plugin install https://github.com/author/Forgekeeper.Scraper.Foo

# Install specific version
forgekeeper plugin install mmf@1.0.0
```

**Install flow:**
1. Fetch `registry.json` (or HEAD of GitHub releases) — use cache if < 24h old
2. Resolve download URL for the requested slug/version
3. Download zip to temp directory
4. Verify SHA256 checksum against `registry.json` entry or `SHA256SUMS` file
5. Extract to `<plugins_dir>/<slug>/`
6. Validate `manifest.json` exists and parses correctly
7. Check SDK compatibility — refuse if incompatible (print helpful message)
8. If hot-reload is enabled, trigger reload; otherwise prompt to restart

### 3.2 Web UI Install

1. Navigate to **Settings → Plugins**
2. Click **Browse Available** (fetches registry.json, renders plugin cards)
3. Find desired plugin, click **Install**
4. UI shows download progress, then confirms success or prints error
5. Plugin appears in installed list, available immediately (hot-reload) or after restart

### 3.3 Manual Install

1. Drop the plugin directory (with `manifest.json`) into `/data/plugins/`
2. Restart Forgekeeper, or call `POST /api/plugins/reload` (if hot-reload enabled)
3. Plugin shows up in **Settings → Plugins** as "manually installed"

Manually installed plugins that aren't in the registry show an **"Unknown source"** badge in the UI.

### 3.4 Docker / docker-compose

For Docker deployments, mount a host directory as the plugins volume:

```yaml
services:
  forgekeeper:
    image: ghcr.io/inxaos/forgekeeper:latest
    volumes:
      - ./plugins:/data/plugins    # user-installed plugins
      - ./data:/data/library        # library database
    environment:
      FORGEKEEPER_PLUGINS_PATH: /data/plugins
      FORGEKEEPER_PLUGINS_BUILTIN_PATH: /app/plugins
```

Forgekeeper loads from both paths: `/app/plugins/` (built-in, read-only image layer) and `/data/plugins/` (user-installed, writable volume). User plugins with the same slug override built-in plugins.

---

## 4. Update Flow

### 4.1 CLI Update

```bash
# Update a specific plugin
forgekeeper plugin update mmf

# Update all installed plugins
forgekeeper plugin update --all

# Check what updates are available (no install)
forgekeeper plugin update --check
```

**Update flow:**
1. Fetch fresh `registry.json` (bypass 24h cache)
2. Compare installed version vs latest in registry
3. Download new version zip
4. Verify checksum
5. Replace old plugin directory (keep backup at `<slug>.bak` until restart confirms clean load)
6. Validate new manifest
7. Check SDK compatibility of new version
8. Hot-reload or prompt restart

### 4.2 Web UI Update

- A **badge** on the Plugins nav item shows the count of available updates
- In **Settings → Plugins**, each outdated plugin shows an **"Update Available"** button with the new version number
- Clicking **Update** triggers the same flow as CLI update
- Option to **Update All** in bulk

### 4.3 Auto-Update

Configurable in `forgekeeper.yml` or environment variables:

```yaml
plugins:
  auto_update:
    enabled: false          # Default: off
    check_interval: 24h     # How often to check for updates
    mode: notify            # "notify" (badge only) or "apply" (auto-install)
    exclude:                # Never auto-update these slugs
      - mmf
```

**Auto-update safety rules:**
- Auto-update is **disabled by default** — opt-in only
- Even in `apply` mode, updates that require a restart are held until the next scheduled restart window (or manual `forgekeeper restart`)
- Updates that bump `min_sdk_version` beyond the current host SDK are blocked and reported as "incompatible — upgrade Forgekeeper first"

### 4.4 Handling Breaking SDK Changes

When a Forgekeeper upgrade includes a new major SDK version:

1. All installed plugins are checked against the new SDK major
2. Incompatible plugins are **disabled** (not deleted) with a clear warning in the UI
3. The UI shows: "Plugin `mmf` v1.0.0 is not compatible with Forgekeeper 2.0 (SDK 2.0). Check for an updated version."
4. Once the plugin author releases a new version with `min_sdk_version: "2.0.0"`, the user can update normally

**Graceful degradation:** If a plugin declares `max_sdk_version: "1.x"` but the host is on SDK 1.5 (a minor bump), it loads fine. The plugin simply doesn't use new 1.5 APIs.

---

## 5. SDK Versioning

### 5.1 PluginSdk Package

The SDK lives in `Forgekeeper.PluginSdk` (NuGet package). Plugin authors reference it in their `.csproj`:

```xml
<PackageReference Include="Forgekeeper.PluginSdk" Version="1.0.0" />
```

### 5.2 Compatibility Rules

| Scenario | Result |
|---|---|
| Plugin sdk_major == host sdk_major | ✅ Load normally |
| Plugin sdk_minor > host sdk_minor | ⚠️ Load with warning (may fail at runtime if new APIs used) |
| Plugin sdk_major > host sdk_major | ❌ Refuse to load |
| Plugin sdk_major < host sdk_major | ❌ Refuse to load (use `--force` to override at own risk) |

### 5.3 SDK Change Policy

- **Patch versions** (1.0.x): Bug fixes to SDK utilities. All plugins work unchanged.
- **Minor versions** (1.x.0): New optional interfaces, new helper methods. Existing `ILibraryScraper` implementations unaffected. Plugins can opt into new features.
- **Major versions** (2.0.0): `ILibraryScraper` interface changed, new required methods, or fundamental type changes. All plugins must be updated.

Major SDK bumps will be announced at least **one minor Forgekeeper release in advance** with a migration guide.

### 5.4 Interface Stability Guarantee

The following are guaranteed stable within a major SDK version:
- `ILibraryScraper` — all methods, signatures, and return types
- `LibraryEntry`, `ScraperContext`, `ScraperResult` — all public properties
- `PluginSdk` namespace structure

New interfaces (e.g., `ILibrarySearchProvider`) introduced in minor versions are **additive only**.

---

## 6. Security

### 6.1 Isolation (Already Implemented)

Plugins run in their own `AssemblyLoadContext`, providing:
- Assembly isolation (no DLL version conflicts between plugins)
- Clean unload path for hot-reload and removal

**Current limitations (to address):**
- `AssemblyLoadContext` does not sandbox network or filesystem access at the OS level
- Plugins run in the same process and CLR as the host

### 6.2 Filesystem Restrictions (v1.1+)

Plugin code **must not** access arbitrary paths. The plugin host will:
- Provide a `IPluginStorage` interface giving the plugin a scoped data directory (e.g., `/data/plugin-data/<slug>/`)
- Log and optionally block `File.*` calls outside allowed paths (via Roslyn analyzer at build time, runtime monitoring at runtime)

### 6.3 Network Access

Plugins receive an `HttpClient` from the host (dependency injection), which:
- Enforces configurable rate limiting per plugin
- Logs all outbound requests for auditing
- Respects a configurable `plugins.allowed_hosts` allowlist (empty = allow all)

Plugins **must not** create their own `HttpClient` instances. The SDK will enforce this via analyzer rules.

### 6.4 Registry Review Process

Plugins submitted to `forgekeeper/plugin-registry` go through a lightweight review:

1. Automated checks (CI): valid manifest.json, download URL resolves, checksum matches, basic DLL scan
2. Human review: code scan for obvious malicious patterns, no hardcoded credentials
3. Maintainer approves and merges PR
4. Plugin appears in registry within minutes

This is intentionally lightweight (like HACS, not like the VS Code Marketplace). Trust is earned — users should treat community plugins like any open-source dependency.

### 6.5 UI Trust Indicators

| Source | Badge |
|---|---|
| Built-in (shipped with image) | 🟢 **Official** |
| Registry-listed plugin | 🔵 **Community** |
| Installed from direct GitHub URL | 🟡 **Unverified** |
| Manually dropped in directory | 🟠 **Manual install** |
| Fails manifest validation | 🔴 **Invalid** |

---

## 7. Plugin Registry Design

### 7.1 Repository Structure

```
forgekeeper/plugin-registry/
├── registry.json          # Master plugin list (auto-generated or manually maintained)
├── plugins/
│   └── mmf.json           # Per-plugin metadata (optional, for richer detail pages)
├── CONTRIBUTING.md        # How to submit a plugin
└── .github/
    └── workflows/
        └── validate.yml   # CI: validate all plugin manifests on PR
```

### 7.2 registry.json Full Schema

```json
{
  "schema_version": "1",
  "updated": "2026-04-18T00:00:00Z",
  "plugins": [
    {
      "slug": "mmf",
      "name": "My Manga Forum Scraper",
      "version": "1.0.0",
      "author": "Damon Prater",
      "author_url": "https://github.com/damonp",
      "description": "Scrapes library data from My Manga Forum (MMF). Supports series, volumes, and reading progress sync.",
      "homepage": "https://github.com/inxaos/Forgekeeper.Scraper.Mmf",
      "source_url": "https://github.com/inxaos/Forgekeeper.Scraper.Mmf",
      "download_url": "https://github.com/inxaos/Forgekeeper.Scraper.Mmf/releases/download/v1.0.0/Forgekeeper.Scraper.Mmf-1.0.0.zip",
      "icon_url": "https://raw.githubusercontent.com/inxaos/Forgekeeper.Scraper.Mmf/main/icon.png",
      "sdk_version": "1.0.0",
      "min_sdk_version": "1.0.0",
      "max_sdk_version": "1.x",
      "min_forgekeeper_version": "1.0.0",
      "checksum_sha256": "abc123def456...",
      "tags": ["manga", "scraper", "official"],
      "license": "MIT",
      "updated": "2026-04-18T00:00:00Z",
      "downloads": 0
    }
  ]
}
```

### 7.3 Forgekeeper Registry Client Behavior

- On first install/update check: fetch `registry.json`, store locally with timestamp
- Subsequent requests: use cached version if age < 24h (configurable)
- Cache stored at: `<data_dir>/registry-cache.json`
- On cache miss or forced refresh: `GET registry.json` with `If-Modified-Since` header
- Network failure: fall back to cache (even if expired), show warning in UI

---

## 8. CLI Commands Reference

```
forgekeeper plugin list
  List all installed plugins with version, source, and status.
  Flags: --json (machine-readable output)

forgekeeper plugin search <query>
  Search the plugin registry by name, description, or tags.
  Fetches registry.json (respects cache).
  Flags: --tag <tag>, --json

forgekeeper plugin install <slug>[@version]
  Install a plugin from the registry.
  Also accepts: https://github.com/<owner>/<repo> (latest GitHub release)
  Flags: --force (skip SDK compatibility check, not recommended)

forgekeeper plugin update [slug]
  Update a specific plugin or all plugins (no slug = --all).
  Flags: --all, --check (report only, no install), --force

forgekeeper plugin remove <slug>
  Uninstall a plugin (deletes directory, disables in config).
  Flags: --yes (skip confirmation)

forgekeeper plugin info <slug>
  Show detailed metadata for an installed or registry plugin.

forgekeeper plugin reload
  Trigger hot-reload of all plugins without restarting.
  (Only if hot-reload is enabled in config)
```

---

## 9. Hot-Reload

Hot-reload allows plugins to be updated without restarting the Forgekeeper service. This is especially useful during development (as currently done with the NFS mount for MMF).

**Hot-reload flow:**
1. File watcher detects change in `/data/plugins/` (or API call)
2. Existing `AssemblyLoadContext` for affected plugin is unloaded
3. New context created, DLL loaded, manifest validated
4. Plugin re-registered in the scraper registry
5. In-flight requests to the old plugin are allowed to complete (configurable timeout)

**Hot-reload limitations:**
- Memory leak risk if old `AssemblyLoadContext` doesn't GC cleanly (DLL locks, static state)
- Not suitable for production at high load — restart is safer for major updates
- Disabled by default in production config; enabled in dev

---

## 10. Implementation Phases

### Phase A — v1.0 (Ship It)

**Goal:** Validate plugins safely. No distribution mechanism needed yet.

- [x] Plugins loaded from `/app/plugins/` (Docker image)
- [x] Plugins loaded from `/data/plugins/` (user volume)
- [ ] `manifest.json` parsing and validation on load
- [ ] SDK version compatibility check (refuse to load incompatible plugins)
- [ ] Basic **Settings → Plugins** page: list loaded plugins with name, version, status
- [ ] Hot-reload via `POST /api/plugins/reload` (dev/debug use)
- [ ] Clear error logging when a plugin fails to load

**Deliverables:** manifest.json schema, validation code, basic plugins UI page.

### Phase B — v1.1 (CLI Distribution)

**Goal:** Install and update plugins without touching the filesystem manually.

- [ ] `forgekeeper plugin install <slug>` — from direct GitHub URL
- [ ] `forgekeeper plugin update [slug]` — from GitHub releases
- [ ] `forgekeeper plugin remove <slug>`
- [ ] `forgekeeper plugin list` and `info`
- [ ] Checksum verification on install
- [ ] Plugin zip packaging convention documented and enforced

**Deliverables:** plugin CLI subcommand, GitHub release packaging guide.

### Phase C — v1.2 (Registry + Web UI)

**Goal:** Discoverable community ecosystem with one-click install.

- [ ] `forgekeeper/plugin-registry` GitHub repo with `registry.json`
- [ ] `forgekeeper plugin search <query>` — searches registry
- [ ] Web UI **Browse Available** page with install/update buttons
- [ ] Update notification badges in sidebar
- [ ] Auto-update check (notify-only mode by default)
- [ ] Registry CI validation workflow
- [ ] CONTRIBUTING.md for registry submissions

**Deliverables:** plugin registry repo, Web UI plugin browser, auto-update check.

### Phase D — Future

**Goal:** Polish, trust, and community growth.

- [ ] Plugin ratings and reviews (GitHub Discussions integration?)
- [ ] Publisher verification (GPG-signed releases)
- [ ] Plugin analytics (opt-in download counts)
- [ ] VS Code-style capability declarations in manifest (what data the plugin reads/writes)
- [ ] Sandboxed network access via configurable allowlist
- [ ] Plugin-contributed UI panels (e.g., MMF-specific settings tab)

---

## 11. Open Questions

1. **Hot-reload in production:** Is it worth the complexity? Or just document "restart to apply updates"?
2. **Registry hosting:** GitHub raw files are free but have rate limits. Use GitHub Pages (with CDN) instead?
3. **Code signing:** Require GPG signatures for registry-listed plugins from day one, or add later?
4. **Plugin data migration:** When a plugin updates and its stored data format changes, how does it migrate? Need `IPluginMigration` interface?
5. **Mono-repo vs per-plugin repos:** For official plugins (MMF), should they live in the main Forgekeeper repo or separate repos?

---

## References

- [Grafana Plugin Development](https://grafana.com/docs/grafana/latest/developers/plugins/)
- [HACS Custom Repositories](https://hacs.xyz/docs/publish/integration)
- [Obsidian Plugin Guidelines](https://docs.obsidian.md/Plugins/Releasing/Plugin+guidelines)
- [VS Code Extension Publishing](https://code.visualstudio.com/api/working-with-extensions/publishing-extension)
- [WordPress Plugin Handbook](https://developer.wordpress.org/plugins/)
- [AssemblyLoadContext Docs](https://docs.microsoft.com/en-us/dotnet/core/dependency-loading/understanding-assemblyloadcontext)
