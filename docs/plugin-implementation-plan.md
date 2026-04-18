# Forgekeeper — Plugin Lifecycle Implementation Plan

> **Version:** 1.0 | **Created:** 2026-04-18 | **Audience:** Engineers building Forgekeeper  
> **Source Documents:** Plugin Lifecycle & Distribution Plan · Implementation Plan (main) · Architecture

---

## Overview

This document is the detailed work breakdown for the Forgekeeper **Plugin Lifecycle** system, spanning four release phases:

| Phase | Target Release | Theme |
|-------|---------------|-------|
| **A** | v1.0 | Ship It — manifest validation, SDK checks, diagnostics |
| **B** | v1.1 | CLI Distribution — install/update/remove from GitHub |
| **C** | v1.2 | Registry + Web UI — community ecosystem, one-click install |
| **D** | Future | Polish — signing, sandboxing, ratings, analytics |

Each phase builds on the previous. Phase A must be complete before B work begins, and so on.

Effort sizing:
- **S** — hours (< 1 day)
- **M** — 1–2 days
- **L** — 3+ days

---

## Phase A — v1.0: Ship It

**Goal:** Load plugins safely. Validate manifest.json. Enforce SDK compatibility. Give operators visibility into plugin health. No distribution mechanism yet — plugins are either bundled in the Docker image or dropped in manually.

### Phase A Work Packages

| ID | Work Package | Effort | Depends On |
|----|-------------|--------|-----------|
| WP-PL1 | manifest.json Parsing & Validation | M | — |
| WP-PL2 | SDK Version Compatibility Check | M | WP-PL1 |
| WP-PL3 | Enhanced Plugins Settings Page | M | WP-PL1, WP-PL2 |
| WP-PL4 | Hot-Reload API | L | WP-PL1, WP-PL2 |
| WP-PL5 | Error Logging & Diagnostics | M | WP-PL1, WP-PL4 |

---

### WP-PL1: manifest.json Parsing & Validation

**Objective:** During plugin discovery in `PluginHostService`, read and validate `manifest.json` from each plugin directory. Store the parsed data on the `LoadedPlugin` record. Reject invalid plugins gracefully — log the error, skip the plugin, don't crash.

**Dependencies:** None (first plugin work package)

**Deliverables:**
- `plugins/manifest.schema.json` — JSON Schema for the manifest format (documented in `plugin-lifecycle.md`)
- `PluginManifest` POCO in `Forgekeeper.Core/Models/`
- `PluginManifestValidator` service in `Forgekeeper.Infrastructure/Services/`
- `PluginHostService` updated to call validator during discovery
- `LoadedPlugin` model updated to carry parsed `PluginManifest?`

**Estimated effort:** M (1–2 days)

**Detailed Tasks:**

| # | Task | Files | Acceptance Criteria |
|---|------|-------|-------------------|
| PL1.1 | Create `PluginManifest` POCO with all fields from §1.2 of plugin-lifecycle.md | `Forgekeeper.Core/Models/PluginManifest.cs` | All required + optional fields present; SemVer fields typed as `string`; `Tags` is `string[]` |
| PL1.2 | Create `plugins/manifest.schema.json` (JSON Schema Draft 7) | `plugins/manifest.schema.json` | Schema validates the example manifest in plugin-lifecycle.md §1.2 with zero errors |
| PL1.3 | Implement `IPluginManifestValidator` interface | `Forgekeeper.Core/Interfaces/IPluginManifestValidator.cs` | Interface defines `PluginManifestValidationResult Validate(string pluginDir)` |
| PL1.4 | Implement `PluginManifestValidator` — read `manifest.json`, deserialize, validate required fields, validate `slug` format (lowercase + hyphens only), validate `version`/`sdk_version`/`min_sdk_version`/`max_sdk_version` are valid SemVer | `Forgekeeper.Infrastructure/Services/PluginManifestValidator.cs` | Missing required field → `ValidationResult.Error`; missing optional field → `ValidationResult.Warning`; invalid SemVer → `ValidationResult.Error` |
| PL1.5 | Add `PluginManifestValidationResult` record with `IsValid`, `Errors[]`, `Warnings[]` | `Forgekeeper.Core/Models/PluginManifestValidationResult.cs` | Distinct error/warning lists; `IsValid` is false when any Errors present |
| PL1.6 | Update `PluginHostService.DiscoverPluginsAsync()` to call `IPluginManifestValidator` for each plugin directory | `Forgekeeper.Infrastructure/Services/PluginHostService.cs` | Invalid manifest → log structured error, skip plugin load, no exception thrown; valid manifest → stored on `LoadedPlugin` |
| PL1.7 | Update `LoadedPlugin` to carry `PluginManifest? Manifest` and `PluginManifestValidationResult? ValidationResult` | `Forgekeeper.Core/Models/LoadedPlugin.cs` | Fields populated after successful validation; null if manifest missing |
| PL1.8 | Register `IPluginManifestValidator` → `PluginManifestValidator` in DI | `Forgekeeper.Api/Program.cs` | `IPluginManifestValidator` resolves from DI container |
| PL1.9 | Unit tests for `PluginManifestValidator` — valid manifest, missing required field, bad slug format, bad SemVer | `Forgekeeper.Tests/PluginManifestValidatorTests.cs` | All tests pass; edge cases covered (empty tags array, null optional fields) |

**Technical Notes:**
- Use `System.Text.Json` for deserialization (already in the project). Use `[JsonPropertyName("slug")]` etc. to map snake_case JSON to PascalCase C# properties.
- SemVer validation: use `System.Version` for strict parsing OR a simple regex `^\d+\.\d+\.\d+$`. The `max_sdk_version` field allows wildcards like `"1.x"` — handle this case specially (parse major only for wildcard versions).
- `PluginHostService` is in `Forgekeeper.Infrastructure/Services/` — this is where `AssemblyLoadContext` loading currently lives. Do not break existing loading behavior; add validation as a pre-check gate.
- Plugins without a `manifest.json` at all should also produce a structured warning (not a hard error in v1.0 — built-in plugins may not have one yet).

---

### WP-PL2: SDK Version Compatibility Check

**Objective:** After manifest validation passes, compare the plugin's declared SDK version requirements against the host SDK version. Refuse to load incompatible plugins with a clear, actionable error message.

**Dependencies:** WP-PL1

**Deliverables:**
- `SdkVersion` constant defined in `Forgekeeper.PluginSdk`
- `SdkCompatibilityChecker` service in `Forgekeeper.Infrastructure/Services/`
- `PluginHostService` updated to call compatibility check after manifest validation
- Config option `plugins.force_load_incompatible` (default: false) for override

**Estimated effort:** M (1–2 days)

**Detailed Tasks:**

| # | Task | Files | Acceptance Criteria |
|---|------|-------|-------------------|
| PL2.1 | Define `SdkVersion` constant (e.g., `public static readonly Version Current = new(1, 0, 0)`) in `Forgekeeper.PluginSdk` | `Forgekeeper.PluginSdk/SdkVersion.cs` | Version constant accessible as `SdkVersion.Current`; value is `1.0.0` |
| PL2.2 | Create `ISdkCompatibilityChecker` interface | `Forgekeeper.Core/Interfaces/ISdkCompatibilityChecker.cs` | Interface defines `SdkCompatibilityResult Check(PluginManifest manifest)` |
| PL2.3 | Implement `SdkCompatibilityChecker` applying the rules from plugin-lifecycle.md §5.2: major match required; minor mismatch = warning; major mismatch = error | `Forgekeeper.Infrastructure/Services/SdkCompatibilityChecker.cs` | Plugin `sdk_major > host_major` → incompatible error; `sdk_major < host_major` → incompatible error (force override available); `sdk_minor > host_minor` → warning; all equal → compatible |
| PL2.4 | Add `SdkCompatibilityResult` record with `IsCompatible`, `RequiresForce`, `Message` | `Forgekeeper.Core/Models/SdkCompatibilityResult.cs` | Distinct states for compatible/warning/hard-incompatible |
| PL2.5 | Add `ForceLoadIncompatible` to `PluginOptions` config class | `Forgekeeper.Infrastructure/Configuration/PluginOptions.cs` | Defaults to `false`; mapped from `plugins:force_load_incompatible` in appsettings |
| PL2.6 | Update `PluginHostService` to call `ISdkCompatibilityChecker` after successful manifest validation; respect `ForceLoadIncompatible` flag | `Forgekeeper.Infrastructure/Services/PluginHostService.cs` | Incompatible plugin with `force=false` → logged error, skipped; `force=true` → logged warning, loaded anyway |
| PL2.7 | Update `LoadedPlugin` to carry `SdkCompatibilityResult? SdkCompatibility` | `Forgekeeper.Core/Models/LoadedPlugin.cs` | Field populated after compatibility check |
| PL2.8 | Unit tests for `SdkCompatibilityChecker` — all matrix scenarios from §5.2 | `Forgekeeper.Tests/SdkCompatibilityCheckerTests.cs` | All six scenarios from the compatibility matrix produce correct results |

**Technical Notes:**
- The `max_sdk_version` wildcard format `"1.x"` means "any 1.y". Parse it by extracting the major version and treating it as a range check: `plugin_sdk_major <= host_sdk_major`. Regular SemVer like `"1.2.0"` means exactly that version constraint.
- Error messages should be operator-friendly: `"Plugin 'mmf' v1.0.0 requires SDK 2.0 but host is SDK 1.0. Refusing to load. Update Forgekeeper or use force_load_incompatible."`.
- The `--force` override exists for development/debugging only. In production it should trigger a `LOG_LEVEL=Warning` event to make misuse visible.

---

### WP-PL3: Enhanced Plugins Settings Page

**Objective:** Update `PluginsView.vue` from a basic list to a rich settings page showing manifest metadata, SDK compatibility status, trust badge, enable/disable toggle, and sync history.

**Dependencies:** WP-PL1 (manifest data), WP-PL2 (SDK compat data)

**Deliverables:**
- `GET /api/v1/plugins` endpoint returns enriched plugin list DTO
- `PluginDto` updated with manifest fields, trust level, compatibility status
- `PluginsView.vue` redesigned with plugin cards
- Trust badge component `PluginTrustBadge.vue`
- SDK compatibility indicator in each card

**Estimated effort:** M (1–2 days)

**Detailed Tasks:**

| # | Task | Files | Acceptance Criteria |
|---|------|-------|-------------------|
| PL3.1 | Extend `PluginDto` with: `Name`, `Version`, `Author`, `Description`, `SdkVersion`, `SdkCompatible`, `SdkWarning`, `TrustLevel`, `EnabledInConfig`, `LastSyncTime`, `ErrorCount`, `ManifestValid` | `Forgekeeper.Core/DTOs/PluginDto.cs` | All fields populated from `LoadedPlugin` data |
| PL3.2 | Add `TrustLevel` enum: `Official`, `Community`, `Unverified`, `Manual`, `Invalid` | `Forgekeeper.Core/Enums/TrustLevel.cs` | Enum values match §6.5 trust indicators in plugin-lifecycle.md |
| PL3.3 | Implement trust level determination logic in `PluginHostService` or a new `PluginTrustEvaluator` — Official = loaded from `/app/plugins/`, Manual = loaded from user data dir with no registry entry, Invalid = manifest validation failed | `Forgekeeper.Infrastructure/Services/PluginTrustEvaluator.cs` | Built-in plugins → Official; user-dir plugins → Manual (until registry added in Phase C); invalid manifest → Invalid |
| PL3.4 | Update `GET /api/v1/plugins` endpoint to return `List<PluginDto>` with all new fields | `Forgekeeper.Api/Endpoints/PluginEndpoints.cs` | Response includes manifest data for plugins with valid manifests; null-safe for plugins without manifests |
| PL3.5 | Redesign `PluginsView.vue` — replace plain table with card grid, each card shows: icon (or default), name, version, author, description, trust badge, SDK compat indicator, enable toggle, last sync time, error count | `Forgekeeper.Web/src/views/PluginsView.vue` | Cards render for all loaded plugins; missing optional fields (icon, description) degrade gracefully |
| PL3.6 | Create `PluginTrustBadge.vue` component — renders colored badge based on `TrustLevel` with emoji per §6.5 | `Forgekeeper.Web/src/components/PluginTrustBadge.vue` | Official=green, Community=blue, Unverified=yellow, Manual=orange, Invalid=red; tooltip shows full label |
| PL3.7 | Create `PluginSdkBadge.vue` component — green checkmark for compatible, yellow warning for minor mismatch, red X for incompatible | `Forgekeeper.Web/src/components/PluginSdkBadge.vue` | All three states render correctly; tooltip shows SDK version details |
| PL3.8 | Add enable/disable toggle that calls `PATCH /api/v1/plugins/{slug}/enabled` | `PluginsView.vue`, `PluginEndpoints.cs` | Toggle updates `plugins.disabled_slugs` in config and re-renders; disabled plugins show greyed-out card |

**Technical Notes:**
- For Phase A, the enable/disable feature writes to a `disabled_slugs` list in `appsettings.json` (or a local override file). The plugin host reads this list on startup and skips loading those slugs. Full hot-disable without restart is Phase D scope.
- Error count comes from the `SyncRun` table — count of `SyncRun` records for this plugin's source with `Status = Failed` in the last 30 days.
- Last sync time comes from the most recent `SyncRun.CompletedAt` for this plugin's source.

---

### WP-PL4: Hot-Reload API

**Objective:** Provide API endpoints to reload all plugins (or a single plugin) without restarting the Forgekeeper service. Uses proper `AssemblyLoadContext` unload + recreate. Gated by a config flag — disabled by default in production.

**Dependencies:** WP-PL1 (manifest must be re-validated on reload), WP-PL2 (SDK compat must be re-checked on reload)

**Deliverables:**
- `POST /api/v1/plugins/reload` — reload all plugins
- `POST /api/v1/plugins/{slug}/reload` — reload single plugin
- Optional file watcher service `PluginDirectoryWatcher` (config flag `plugins.watch_enabled`)
- Proper `AssemblyLoadContext` lifecycle management

**Estimated effort:** L (3+ days)

**Detailed Tasks:**

| # | Task | Files | Acceptance Criteria |
|---|------|-------|-------------------|
| PL4.1 | Add `HotReloadEnabled` and `WatchEnabled` to `PluginOptions` | `Forgekeeper.Infrastructure/Configuration/PluginOptions.cs` | Both default to `false`; mapped from `plugins:hot_reload_enabled` and `plugins:watch_enabled` |
| PL4.2 | Refactor `PluginHostService` to support `UnloadPlugin(slug)` — unregisters the plugin from the scraper registry, calls `AssemblyLoadContext.Unload()`, removes from loaded plugins list | `Forgekeeper.Infrastructure/Services/PluginHostService.cs` | `UnloadPlugin("mmf")` removes the plugin from all internal registries; subsequent `GetPlugin("mmf")` returns null |
| PL4.3 | Implement `ReloadPlugin(slug)` — calls `UnloadPlugin`, then re-runs discovery + validation + loading for that slug's directory | `Forgekeeper.Infrastructure/Services/PluginHostService.cs` | Plugin is unloaded then reloaded with fresh `AssemblyLoadContext`; manifest re-read from disk |
| PL4.4 | Implement `ReloadAllPlugins()` — reloads all currently tracked plugin directories | `Forgekeeper.Infrastructure/Services/PluginHostService.cs` | All plugins reloaded in sequence; failures in one plugin don't block others |
| PL4.5 | Add reload endpoints to `PluginEndpoints.cs`: `POST /api/v1/plugins/reload` and `POST /api/v1/plugins/{slug}/reload` | `Forgekeeper.Api/Endpoints/PluginEndpoints.cs` | `hot_reload_enabled=false` → 501 Not Implemented with clear message; `hot_reload_enabled=true` → triggers reload, returns 200 with reload summary |
| PL4.6 | Add `IPluginDirectoryWatcher` interface and `PluginDirectoryWatcher` implementation using `FileSystemWatcher` on the user plugins directory | `Forgekeeper.Infrastructure/Services/PluginDirectoryWatcher.cs` | Watcher triggers `ReloadPlugin(slug)` on new/changed DLL or manifest.json; debounced (500ms) to avoid multiple triggers per save |
| PL4.7 | Register `PluginDirectoryWatcher` as a hosted service when `WatchEnabled=true` | `Forgekeeper.Api/Program.cs` | Service starts with app when enabled; does not register when disabled (no `FileSystemWatcher` created) |
| PL4.8 | Handle in-flight request safety — `PluginHostService` tracks active execution count per plugin; `UnloadPlugin` waits up to 5s for in-flight requests to complete before forcing unload | `Forgekeeper.Infrastructure/Services/PluginHostService.cs` | Active scrape operations are allowed to finish; unload proceeds after drain or 5s timeout with a warning log |
| PL4.9 | Integration test: load plugin, call reload endpoint, verify plugin is available after reload | `Forgekeeper.Tests/PluginHotReloadTests.cs` | Plugin metadata unchanged after reload of same version; plugin successfully loaded with new manifest after manifest change |

**Technical Notes:**
- `AssemblyLoadContext` unload is non-deterministic in .NET — it's GC-dependent. Log a warning if `IsAlive` is true after 1s but don't block on it.
- `FileSystemWatcher` does not work reliably over SMB/NFS mounts. The config docs should note this — use the API endpoint for hot-reload in Docker/NFS dev setups (which is the current MMF dev workflow).
- In-flight request tracking: use `Interlocked.Increment/Decrement` on a counter per `LoadedPlugin`. The `ILibraryScraper` wrapper increments on entry, decrements on exit (finally block).
- Keep the watcher disabled (`watch_enabled=false`) in the default `appsettings.json` to avoid surprise reloads in production.

---

### WP-PL5: Error Logging & Diagnostics

**Objective:** Structured logging for all plugin lifecycle events. A diagnostics endpoint for per-plugin history. Prometheus metrics for plugin health.

**Dependencies:** WP-PL1 (manifest validation results), WP-PL4 (reload history)

**Deliverables:**
- Structured `Serilog` event templates for plugin lifecycle events
- `GET /api/v1/plugins/{slug}/diagnostics` endpoint
- `PluginDiagnosticsDto` with load history, errors, manifest validation results
- Prometheus metrics: `forgekeeper_plugin_loaded`, `forgekeeper_plugin_errors_total`

**Estimated effort:** M (1–2 days)

**Detailed Tasks:**

| # | Task | Files | Acceptance Criteria |
|---|------|-------|-------------------|
| PL5.1 | Define structured log event templates for: `PluginLoaded`, `PluginLoadFailed`, `PluginUnloaded`, `PluginReloaded`, `PluginManifestInvalid`, `PluginSdkIncompatible` | `Forgekeeper.Infrastructure/Services/PluginHostService.cs` | All events emitted with `{Slug}`, `{Version}`, `{Reason}` structured properties; searchable in log output |
| PL5.2 | Create `PluginLoadEvent` record for in-memory event history: `Timestamp`, `EventType`, `Slug`, `Version`, `Message`, `Errors[]` | `Forgekeeper.Core/Models/PluginLoadEvent.cs` | Record captures all fields needed for diagnostics response |
| PL5.3 | Add `PluginEventHistory` to `PluginHostService` — circular buffer of last 100 events per plugin slug (use `ConcurrentDictionary<string, Queue<PluginLoadEvent>>`) | `Forgekeeper.Infrastructure/Services/PluginHostService.cs` | Events accumulate across reloads; oldest events dropped after 100 per slug |
| PL5.4 | Create `PluginDiagnosticsDto` with: `Slug`, `CurrentStatus`, `LoadedAt`, `ManifestValidation`, `SdkCompatibility`, `RecentEvents[]`, `SyncRunSummary` (last 10 sync runs) | `Forgekeeper.Core/DTOs/PluginDiagnosticsDto.cs` | DTO fully populated from event history and SyncRun table |
| PL5.5 | Add `GET /api/v1/plugins/{slug}/diagnostics` endpoint | `Forgekeeper.Api/Endpoints/PluginEndpoints.cs` | Returns 200 with `PluginDiagnosticsDto` for loaded plugin; 404 if slug not recognized |
| PL5.6 | Add Prometheus metrics using `prometheus-net`: `forgekeeper_plugin_loaded` (gauge, label: `slug`), `forgekeeper_plugin_errors_total` (counter, labels: `slug`, `error_type`) | `Forgekeeper.Infrastructure/Services/PluginHostService.cs` | Metrics visible at `GET /metrics`; gauge increments on load, decrements on unload; counter increments on load failure |
| PL5.7 | Update `/api/v1/plugins` list response to include a `HealthSummary` field: `{ "status": "ok|degraded|failed", "lastError": "...", "errorCount": N }` | `Forgekeeper.Api/Endpoints/PluginEndpoints.cs` | Status `ok` = loaded cleanly; `degraded` = loaded with warnings; `failed` = failed to load |

**Technical Notes:**
- `prometheus-net` should already be in the project (used by `SyncRun` metrics). Add plugin metrics alongside existing ones.
- Circular buffer for event history keeps memory bounded. 100 events per plugin × ~500 bytes per event = ~50KB worst case. Acceptable.
- The diagnostics endpoint is intentionally un-authenticated in v1.0 (same as `/metrics`). Production deployments should put it behind the API auth layer when that's added.

---

## Phase B — v1.1: CLI Distribution

**Goal:** Install, update, and remove plugins without touching the filesystem manually. Downloads from GitHub releases, verifies checksums, enforces SDK compatibility before install.

### Phase B Work Packages

| ID | Work Package | Effort | Depends On |
|----|-------------|--------|-----------|
| WP-PL6 | Plugin CLI Subcommand Scaffold | S | Phase A complete |
| WP-PL7 | GitHub Release Resolver | M | WP-PL6 |
| WP-PL8 | Download, Verify & Extract | M | WP-PL7 |
| WP-PL9 | Plugin Remove | S | WP-PL8 |
| WP-PL10 | Plugin List & Info | S | WP-PL6 |

---

### WP-PL6: Plugin CLI Subcommand Scaffold

**Objective:** Add `forgekeeper plugin <subcommand>` to the CLI. No functionality yet — just the command structure, help text, and routing.

**Estimated effort:** S (hours)

**Tasks:**
- Add `PluginCommand` to the CLI command tree (under `forgekeeper`)
- Subcommands: `install`, `update`, `remove`, `list`, `info`, `reload`
- Each subcommand has `--help` with usage examples matching §8 of plugin-lifecycle.md
- Global `--json` flag for machine-readable output
- Implement `forgekeeper plugin list` — calls `GET /api/v1/plugins` and renders as table or JSON

**Acceptance Criteria:**
- `forgekeeper plugin --help` shows all subcommands
- `forgekeeper plugin list` prints installed plugins with version and status
- `forgekeeper plugin list --json` returns valid JSON array

---

### WP-PL7: GitHub Release Resolver

**Objective:** Given a plugin slug (from registry — Phase C) OR a full GitHub repo URL, resolve the download URL for the requested version.

**Estimated effort:** M (1–2 days)

**Tasks:**
- `IGitHubReleaseResolver` interface + `GitHubReleaseResolver` implementation
- Input: `owner/repo` or `https://github.com/owner/repo`, optional version tag
- Output: download URL, version string, checksum file URL
- Supports `latest` (no version specified) and specific version (`mmf@1.0.0`)
- Respects GitHub API rate limits — use unauthenticated requests with proper User-Agent header
- Cache resolved metadata for 1 hour to avoid hammering GitHub

**Acceptance Criteria:**
- `forgekeeper plugin install https://github.com/your-org/Forgekeeper.Scraper.Mmf` resolves latest release
- `forgekeeper plugin install https://github.com/.../Mmf@1.0.0` resolves specific tag
- Rate limit hit → clear error message with retry-after suggestion
- No network → clear offline error

---

### WP-PL8: Download, Verify & Extract

**Objective:** Download the plugin zip, verify SHA256 checksum, extract to the plugins directory. Validate manifest and SDK compatibility before committing.

**Estimated effort:** M (1–2 days)

**Tasks:**
- Download zip to temp directory with progress indicator
- Locate and parse `SHA256SUMS` file from the GitHub release assets
- Verify checksum — abort on mismatch with clear error
- Extract to `<plugins_dir>/<slug>/` — overwrite if already exists (update flow)
- Keep `<slug>.bak/` backup of previous version until new version loads cleanly
- Call manifest validator + SDK compatibility checker before declaring success
- Trigger hot-reload if enabled, otherwise print restart prompt
- Clean up temp files and backup on success

**Acceptance Criteria:**
- Successful install: plugin appears in `forgekeeper plugin list` without restart (hot-reload) or after restart
- Checksum mismatch: temp files cleaned up, plugins directory unchanged, error printed
- SDK incompatible: zip downloaded and verified but not installed; clear error with version info
- Update: old version backed up, new version installed; backup removed after clean load

---

### WP-PL9: Plugin Remove

**Objective:** Uninstall a plugin — delete its directory and disable it in config.

**Estimated effort:** S (hours)

**Tasks:**
- `forgekeeper plugin remove <slug>` with `--yes` flag to skip confirmation
- Interactive confirmation prompt: "Remove plugin 'mmf' v1.0.0? This will delete /data/plugins/mmf/ [y/N]"
- Calls `UnloadPlugin(slug)` via hot-reload API if enabled
- Deletes `<plugins_dir>/<slug>/` directory
- Adds slug to `disabled_slugs` config as a safety net (prevents re-ghost-load)
- Cannot remove built-in plugins (those in `/app/plugins/`) — print informative error

**Acceptance Criteria:**
- Removed plugin no longer appears in `forgekeeper plugin list`
- Attempting to remove a built-in plugin prints: "Cannot remove built-in plugin 'mmf'. Disable it in config instead."
- `--yes` flag skips prompt (for scripting/CI)

---

### WP-PL10: Plugin List & Info

**Objective:** Rich CLI views for plugin metadata.

**Estimated effort:** S (hours)

**Tasks:**
- `forgekeeper plugin list` — table: Slug | Name | Version | Source | Status | SDK Compat
- `forgekeeper plugin info <slug>` — full details including manifest fields, trust level, load history summary
- Both support `--json` flag
- `forgekeeper plugin update --check` — compare installed versions against GitHub releases, print available updates without installing

**Acceptance Criteria:**
- `info` shows all manifest fields
- `update --check` prints "mmf: 1.0.0 → 1.1.0 available" for out-of-date plugins
- `--json` outputs valid JSON parseable by `jq`

---

## Phase C — v1.2: Registry + Web UI

**Goal:** Community discoverability. One-click install from the settings page. Auto-update notifications. Central `forgekeeper/plugin-registry` repo.

### Phase C Work Packages

| ID | Work Package | Effort | Depends On |
|----|-------------|--------|-----------|
| WP-PL11 | Plugin Registry Repo | M | Phase B complete |
| WP-PL12 | Registry Client | M | WP-PL11 |
| WP-PL13 | Web UI Plugin Browser | L | WP-PL12 |
| WP-PL14 | Auto-Update Check | M | WP-PL12 |
| WP-PL15 | Update Notifications in UI | S | WP-PL14 |

---

### WP-PL11: Plugin Registry Repo

**Objective:** Create the `forgekeeper/plugin-registry` GitHub repository with `registry.json`, contributing guide, and CI validation.

**Estimated effort:** M (1–2 days)

**Key deliverables:**
- `registry.json` following the schema in plugin-lifecycle.md §7.2
- `CONTRIBUTING.md` — how to submit a plugin via PR (validate manifest, GitHub release asset conventions, checksum requirement)
- `.github/workflows/validate.yml` — CI that validates all entries in `registry.json` on every PR: parse manifest, check download URL resolves, verify checksum matches
- Initial entry: MMF scraper plugin
- GitHub Pages serving `registry.json` at a stable URL

**Acceptance Criteria:**
- `registry.json` is valid against schema and includes MMF entry
- CI fails PR with descriptive error if any registry entry has broken download URL or invalid manifest
- Stable public URL for `registry.json` (GitHub Pages or raw.githubusercontent.com)

---

### WP-PL12: Registry Client

**Objective:** `RegistryClient` service that fetches, caches, and queries `registry.json`.

**Estimated effort:** M (1–2 days)

**Key deliverables:**
- `IRegistryClient` interface with `SearchAsync(query)`, `GetAllAsync()`, `GetBySlugAsync(slug)`, `RefreshAsync()`
- `RegistryClient` implementation — fetches `registry.json` from configured URL, caches locally at `<data_dir>/registry-cache.json` with 24h TTL
- Uses `If-Modified-Since` header to avoid re-downloading unchanged registry
- Falls back to stale cache on network failure (logs warning)
- Registry URL configurable in `plugins.registry_url` (defaults to the official GitHub Pages URL)
- `forgekeeper plugin search <query>` wired up via `RegistryClient`

**Acceptance Criteria:**
- First call fetches and caches registry; subsequent calls within 24h use cache
- Offline with valid cache → uses stale cache, prints "(cached)" notice
- Offline with no cache → clear error with suggestion to retry when online
- `forgekeeper plugin install mmf` now resolves slug via registry to get download URL (no longer requires full GitHub URL)

---

### WP-PL13: Web UI Plugin Browser

**Objective:** "Browse Available" tab in Settings → Plugins showing community plugins with install/update buttons.

**Estimated effort:** L (3+ days)

**Key deliverables:**
- New `PluginBrowserView.vue` tab (or panel) in settings
- Plugin cards from registry: icon, name, version, author, description, tags, trust badge
- "Install" button → calls `POST /api/v1/plugins/install` (new endpoint, wraps CLI logic)
- "Update Available" badge on installed plugins that have newer versions in registry
- "Update" button per plugin + "Update All" bulk action
- Search/filter by name, tag, author
- Download progress indication during install

**Acceptance Criteria:**
- Plugin browser loads within 2 seconds (uses cached registry on repeat visits)
- Installing a plugin from the browser adds it to the installed list without page refresh
- "Update All" updates all out-of-date plugins sequentially, shows per-plugin progress
- Search filters results client-side (no additional network requests)

---

### WP-PL14: Auto-Update Check

**Objective:** Background service that periodically checks for plugin updates and records findings.

**Estimated effort:** M (1–2 days)

**Key deliverables:**
- `PluginUpdateChecker` hosted service — runs on configurable interval (default 24h)
- Compares installed plugin versions against registry latest versions
- Stores `AvailableUpdate` records in DB or in-memory cache
- Config: `plugins.auto_update.enabled` (default false), `plugins.auto_update.check_interval` (default 24h), `plugins.auto_update.mode` (notify or apply), `plugins.auto_update.exclude[]`
- In `notify` mode: records available updates, triggers UI badge (§ WP-PL15)
- In `apply` mode: automatically downloads and installs compatible updates; holds updates requiring restart until next startup window

**Acceptance Criteria:**
- Update check runs silently in background; never blocks request handling
- Available updates reflected in `GET /api/v1/plugins` response within one check interval
- `apply` mode does not auto-install if `min_sdk_version` bumped beyond current host SDK
- `exclude` list respected — excluded plugins never auto-updated

---

### WP-PL15: Update Notification Badges

**Objective:** Show update count badge on the Plugins nav item; per-plugin "Update Available" button in settings.

**Estimated effort:** S (hours)

**Key deliverables:**
- `GET /api/v1/plugins/updates` endpoint — returns list of `{ slug, currentVersion, availableVersion }`
- Sidebar nav badge on "Plugins" showing count of available updates
- Per-plugin "Update Available" pill in plugin cards
- Badge clears after all plugins updated

**Acceptance Criteria:**
- Badge shows correct count after update check runs
- Badge disappears (or shows 0) after all updates applied
- No badge when all plugins are up to date

---

## Phase D — Future

**Goal:** Trust, polish, and community ecosystem maturity. No strict release targeting — implement when the community is large enough to justify the complexity.

### Phase D Work Packages

| ID | Work Package | Priority | Notes |
|----|-------------|----------|-------|
| WP-PL16 | GPG-Signed Releases | High | Publisher verification for registry-listed plugins. Plugin authors sign their release zips; Forgekeeper verifies signature against a trusted keyring. Required for `Official` trust level. |
| WP-PL17 | Sandboxed Network Access | Medium | Inject `HttpClient` via `IPluginStorage` DI with per-plugin rate limiting and request logging. Block plugins that create their own `HttpClient`. Enforce via Roslyn analyzer at build time. |
| WP-PL18 | Plugin-Scoped Storage | Medium | `IPluginStorage` interface giving plugins a scoped data directory at `/data/plugin-data/<slug>/`. Prevents cross-plugin filesystem access. Needed before any plugin can persist local state. |
| WP-PL19 | Plugin Capability Declarations | Medium | Manifest field `capabilities: ["network", "filesystem", "scraper"]` — UI shows what a plugin can do before install. Basis for future permission prompts. |
| WP-PL20 | Plugin Ratings & Reviews | Low | GitHub Discussions integration for community feedback. Ratings surfaced in plugin browser cards. Requires meaningful community size to be useful. |
| WP-PL21 | Plugin Analytics (Opt-in) | Low | Opt-in download count tracking via registry. Allows sorting by popularity. Privacy-preserving: counts only, no user data. |
| WP-PL22 | Plugin-Contributed UI Panels | Low | Allow plugins to inject Vue components into specific UI extension points (e.g., an MMF-specific settings panel). Complex — requires a UI plugin API with sandboxed component mounting. |
| WP-PL23 | Plugin Data Migration API | Medium | `IPluginMigration` interface for plugins to handle data format changes across plugin versions. Needed once any plugin stores structured data in the DB or file system. |
| WP-PL24 | Mono-repo vs Per-plugin Decision | S | Resolve open question: should official plugins (MMF) live in the main Forgekeeper repo or separate repos? Decision affects CI, versioning, and the packaging pipeline. |

---

## Cross-Cutting Concerns

### Testing Strategy

| Phase | Test Types |
|-------|-----------|
| A | Unit tests for validator + compat checker; integration tests for PluginHostService; frontend component tests for badges |
| B | Unit tests for GitHub resolver + checksum verifier; CLI integration tests (mock GitHub API) |
| C | Integration tests for registry client (mock registry server); E2E tests for browser install flow |
| D | Security tests for sandboxing; signing verification tests |

### Configuration Reference

All plugin config lives under the `plugins:` key in `appsettings.json`:

```json
{
  "plugins": {
    "path": "/data/plugins",
    "builtin_path": "/app/plugins",
    "hot_reload_enabled": false,
    "watch_enabled": false,
    "force_load_incompatible": false,
    "registry_url": "https://forgekeeper.github.io/plugin-registry/registry.json",
    "auto_update": {
      "enabled": false,
      "check_interval": "24:00:00",
      "mode": "notify",
      "exclude": []
    },
    "disabled_slugs": []
  }
}
```

### Files Created/Modified Summary

**Phase A:**
- NEW: `Forgekeeper.Core/Models/PluginManifest.cs`
- NEW: `Forgekeeper.Core/Models/PluginManifestValidationResult.cs`
- NEW: `Forgekeeper.Core/Models/SdkCompatibilityResult.cs`
- NEW: `Forgekeeper.Core/Models/PluginLoadEvent.cs`
- NEW: `Forgekeeper.Core/Models/PluginDiagnosticsDto.cs`  *(goes in DTOs/)*
- NEW: `Forgekeeper.Core/Interfaces/IPluginManifestValidator.cs`
- NEW: `Forgekeeper.Core/Interfaces/ISdkCompatibilityChecker.cs`
- NEW: `Forgekeeper.Core/Enums/TrustLevel.cs`
- NEW: `Forgekeeper.Infrastructure/Services/PluginManifestValidator.cs`
- NEW: `Forgekeeper.Infrastructure/Services/SdkCompatibilityChecker.cs`
- NEW: `Forgekeeper.Infrastructure/Services/PluginTrustEvaluator.cs`
- NEW: `Forgekeeper.Infrastructure/Services/PluginDirectoryWatcher.cs`
- NEW: `Forgekeeper.PluginSdk/SdkVersion.cs`
- NEW: `plugins/manifest.schema.json`
- MOD: `Forgekeeper.Core/Models/LoadedPlugin.cs` — add `Manifest`, `ValidationResult`, `SdkCompatibility`
- MOD: `Forgekeeper.Core/DTOs/PluginDto.cs` — add manifest fields + trust + compat
- MOD: `Forgekeeper.Infrastructure/Services/PluginHostService.cs` — validation gate, reload, event history
- MOD: `Forgekeeper.Infrastructure/Configuration/PluginOptions.cs` — new config fields
- MOD: `Forgekeeper.Api/Endpoints/PluginEndpoints.cs` — reload + diagnostics endpoints
- MOD: `Forgekeeper.Api/Program.cs` — register new services
- MOD: `Forgekeeper.Web/src/views/PluginsView.vue` — card redesign
- NEW: `Forgekeeper.Web/src/components/PluginTrustBadge.vue`
- NEW: `Forgekeeper.Web/src/components/PluginSdkBadge.vue`
- NEW: `Forgekeeper.Tests/PluginManifestValidatorTests.cs`
- NEW: `Forgekeeper.Tests/SdkCompatibilityCheckerTests.cs`
- NEW: `Forgekeeper.Tests/PluginHotReloadTests.cs`

---

## Open Questions

1. **Hot-reload in production:** File watcher over NFS is unreliable. Document clearly: use `POST /api/v1/plugins/reload` in Docker deployments, watcher only for bare-metal dev.
2. **Registry hosting:** GitHub Pages is free and has a CDN. Prefer over raw.githubusercontent.com to avoid rate limits.
3. **Code signing (Phase D):** Don't require GPG from day one — adds friction for early community contributors. Add in Phase D once the ecosystem proves itself.
4. **Plugin data migration (Phase D):** Define `IPluginMigration` when the first plugin needs to migrate stored data. Don't design it abstractly before a concrete use case exists.
5. **Mono-repo decision (WP-PL24):** Recommendation is per-plugin repos for community plugins, mono-repo for official (MMF stays in `plugins/Forgekeeper.Scraper.Mmf/`). Revisit if this creates excessive maintenance overhead.

---

## References

- [Plugin Lifecycle & Distribution Plan](plans/forgekeeper/plugin-lifecycle) — the architecture this plan implements
- [Forgekeeper Implementation Plan](plans/forgekeeper/implementation) — main project work breakdown (WP1–WP14)
- [Plugin Development Guide](plans/forgekeeper/plugin-development) — how to write a plugin
- [AssemblyLoadContext Docs](https://docs.microsoft.com/en-us/dotnet/core/dependency-loading/understanding-assemblyloadcontext)
- [HACS Plugin Pattern](https://hacs.xyz/docs/publish/integration) — inspiration for registry design
