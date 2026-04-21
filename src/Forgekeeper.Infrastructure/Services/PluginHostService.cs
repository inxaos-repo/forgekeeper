using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Data;
using Forgekeeper.PluginSdk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Forgekeeper.Infrastructure.Services;

/// <summary>
/// Background service that discovers, loads, and manages scraper plugins.
/// Plugins are loaded from DLLs in the plugins/ directory using isolated AssemblyLoadContexts.
/// </summary>
public class PluginHostService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<PluginHostService> _logger;
    private readonly ManifestValidationService _manifestValidator;
    private readonly SdkCompatibilityChecker _sdkChecker;
    private readonly ConcurrentDictionary<string, LoadedPlugin> _plugins = new();
    private readonly ConcurrentDictionary<string, PluginSyncStatus> _syncStatuses = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _syncLocks = new();
    private readonly string _pluginsDirectory;
    private readonly string _builtinPluginsDirectory;
    private readonly string _sourcesDirectory;
    private CancellationToken _appStoppingToken;
    private readonly bool _forceLoadIncompatible;
    private readonly bool _hotReloadEnabled;
    private readonly bool _preferBundled;
    private readonly bool _warnOnVersionDrift;

    public PluginHostService(
        IServiceProvider services,
        ILogger<PluginHostService> logger,
        IConfiguration configuration,
        ManifestValidationService manifestValidator,
        SdkCompatibilityChecker sdkChecker)
    {
        _services = services;
        _logger = logger;
        _manifestValidator = manifestValidator;
        _sdkChecker = sdkChecker;
        _builtinPluginsDirectory = configuration["Forgekeeper:BuiltinPluginsDirectory"] ?? "/app/plugins";
        _pluginsDirectory = configuration["Forgekeeper:PluginsDirectory"] ?? "/data/plugins";
        _sourcesDirectory = configuration["Forgekeeper:SourcesDirectory"] ?? "/mnt/3dprinting/sources";
        _forceLoadIncompatible = configuration.GetValue<bool>("Plugins:ForceLoadIncompatible", false);
        _hotReloadEnabled = configuration.GetValue<bool>("Plugins:HotReloadEnabled", false);
        _preferBundled = configuration.GetValue<bool>("Plugins:PreferBundled", false);
        _warnOnVersionDrift = configuration.GetValue<bool>("Plugins:WarnOnVersionDrift", false);
    }

    /// <summary>Get all loaded plugins.</summary>
    public IReadOnlyDictionary<string, LoadedPlugin> Plugins => _plugins;

    /// <summary>Whether hot-reload is enabled via config.</summary>
    public bool HotReloadEnabled => _hotReloadEnabled;

    /// <summary>Get sync status for a plugin.</summary>
    public PluginSyncStatus? GetSyncStatus(string slug) =>
        _syncStatuses.TryGetValue(slug, out var status) ? status : null;

    /// <summary>Get a loaded plugin by slug.</summary>
    public ILibraryScraper? GetPlugin(string slug) =>
        _plugins.TryGetValue(slug, out var loaded) ? loaded.Scraper : null;

    /// <summary>
    /// Unload a plugin by slug: removes it from the registry and unloads its AssemblyLoadContext.
    /// Used by the remove API endpoint after deleting the plugin directory.
    /// </summary>
    public void UnloadPlugin(string slug)
    {
        if (_plugins.TryRemove(slug, out var plugin))
        {
            _logger.LogInformation("Unloading plugin: {Slug}", slug);
            plugin.LoadContext.Unload();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            _logger.LogInformation("Plugin '{Slug}' unloaded", slug);
        }
    }

    /// <summary>Whether a plugin is currently syncing.</summary>
    public bool IsPluginSyncing(string slug) =>
        _syncStatuses.TryGetValue(slug, out var s) && s.IsRunning;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Plugin host starting — builtin: {BuiltinDir}, installed: {InstalledDir}",
            _builtinPluginsDirectory, _pluginsDirectory);

        _appStoppingToken = stoppingToken;
        await DiscoverPluginsAsync(stoppingToken);

        _logger.LogInformation("Loaded {Count} plugin(s): {Slugs}",
            _plugins.Count, string.Join(", ", _plugins.Keys));

        // Periodic sync loop — check every 5 minutes if any plugin needs a sync
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            foreach (var (slug, loaded) in _plugins)
            {
                if (stoppingToken.IsCancellationRequested) break;

                // Use TryWait(0) on the per-plugin semaphore to skip if a sync is already running
                var loopSemaphore = _syncLocks.GetOrAdd(slug, _ => new SemaphoreSlim(1, 1));
                if (!loopSemaphore.Wait(0)) continue;
                loopSemaphore.Release(); // Release immediately — RunSyncAsync will re-acquire

                var status = _syncStatuses.GetOrAdd(slug, _ => new PluginSyncStatus());

                var interval = await GetSyncIntervalAsync(slug, stoppingToken);
                if (interval <= TimeSpan.Zero) continue;

                if (DateTime.UtcNow - status.LastSyncAt < interval) continue;

                _ = Task.Run(() => RunSyncAsync(slug, stoppingToken, 0), stoppingToken);
            }
        }
    }

    /// <summary>Trigger a manual sync for a plugin.</summary>
    /// <param name="slug">Plugin slug.</param>
    /// <param name="resume">If true, resumes the last incomplete sync from its LastProcessedIndex.</param>
    /// <param name="ct">Cancellation token for the HTTP request (short-lived).</param>
    public async Task TriggerSyncAsync(string slug, bool resume, CancellationToken ct)
    {
        if (!_plugins.ContainsKey(slug))
            throw new InvalidOperationException($"Plugin '{slug}' not found");

        // Acquire the per-plugin sync lock to atomically check + set IsRunning
        var semaphore = _syncLocks.GetOrAdd(slug, _ => new SemaphoreSlim(1, 1));
        if (!await semaphore.WaitAsync(0, ct))
            throw new InvalidOperationException($"Sync for '{slug}' is already running");

        var status = _syncStatuses.GetOrAdd(slug, _ => new PluginSyncStatus());
        if (status.IsRunning)
        {
            semaphore.Release();
            throw new InvalidOperationException($"Sync for '{slug}' is already running");
        }
        status.IsRunning = true; // Set while holding the lock
        semaphore.Release();     // Release before starting background task

        // If resuming, find the last incomplete SyncRun and use its LastProcessedIndex
        int startIndex = 0;
        if (resume)
        {
            var dbFactory = _services.GetRequiredService<IDbContextFactory<ForgeDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var lastRun = await db.SyncRuns
                .Where(r => r.PluginSlug == slug && (r.Status == "running" || r.Status == "failed") && r.LastProcessedIndex > 0)
                .OrderByDescending(r => r.StartedAt)
                .FirstOrDefaultAsync(ct);
            if (lastRun != null)
            {
                startIndex = lastRun.LastProcessedIndex;
                _logger.LogInformation("[{Slug}] Resuming sync from index {Index} (last run: {Id})", slug, startIndex, lastRun.Id);
            }
        }

        // Use a long-lived token (not the HTTP request's CT which times out with nginx)
        // The sync runs in the background and can take minutes for FlareSolverr CF solve + login
        // Create a linked CTS: cancels on app shutdown OR user cancel request
        var syncCts = CancellationTokenSource.CreateLinkedTokenSource(_appStoppingToken);
        status.SyncCts = syncCts;
        _ = Task.Run(() => RunSyncAsync(slug, syncCts.Token, startIndex));
    }

    /// <summary>Handle an auth callback routed from the web server.</summary>
    /// <summary>Cancel a running sync for a plugin.</summary>
    public bool CancelSync(string slug)
    {
        var status = _syncStatuses.GetOrAdd(slug, _ => new PluginSyncStatus());
        if (!status.IsRunning || status.SyncCts == null) return false;
        _logger.LogInformation("[{Slug}] Sync cancellation requested", slug);
        status.SyncCts.Cancel();
        return true;
    }

    public async Task<AuthResult> HandleAuthCallbackAsync(string slug, IDictionary<string, string> callbackParams, CancellationToken ct)
    {
        if (!_plugins.TryGetValue(slug, out var loaded))
            return AuthResult.Failed($"Plugin '{slug}' not found");

        var context = await BuildPluginContextAsync(slug, loaded.Scraper, ct);
        return await loaded.Scraper.HandleAuthCallbackAsync(context, callbackParams, ct);
    }

    private async Task RunSyncAsync(string slug, CancellationToken ct, int startIndex = 0)
    {
        if (!_plugins.TryGetValue(slug, out var loaded)) return;

        // Acquire per-plugin sync lock with WaitAsync(0) — return immediately if locked
        var semaphore = _syncLocks.GetOrAdd(slug, _ => new SemaphoreSlim(1, 1));
        if (!await semaphore.WaitAsync(0))
        {
            _logger.LogDebug("[{Slug}] Sync skipped — another sync is already in progress", slug);
            return;
        }

        var status = _syncStatuses.GetOrAdd(slug, _ => new PluginSyncStatus());
        status.IsRunning = true;
        status.LastSyncAt = DateTime.UtcNow;
        status.Error = null;

        // Create SyncRun record
        var syncRunId = Guid.NewGuid();
        var startedAt = DateTime.UtcNow;
        var dbFactory = _services.GetRequiredService<IDbContextFactory<ForgeDbContext>>();

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            db.SyncRuns.Add(new Core.Models.SyncRun
            {
                Id = syncRunId,
                PluginSlug = slug,
                StartedAt = startedAt,
                Status = "running",
                LastProcessedIndex = startIndex,
            });
            await db.SaveChangesAsync(ct);
        }

        int scraped = 0, failed = 0, skipped = 0;
        string syncStatus = "completed";
        string? syncError = null;

        try
        {
            var context = await BuildPluginContextAsync(slug, loaded.Scraper, ct);
            var progress = new Progress<ScrapeProgress>(p =>
            {
                status.CurrentProgress = p;
                _logger.LogDebug("[{Slug}] {Status} {Current}/{Total} {Item}",
                    slug, p.Status, p.Current, p.Total, p.CurrentItem);
            });

            // Rebuild context with real progress reporter
            context = new PluginContext
            {
                SourceDirectory = context.SourceDirectory,
                Config = context.Config,
                HttpClient = context.HttpClient,
                Logger = context.Logger,
                TokenStore = context.TokenStore,
                Progress = progress,
            };

            // Authenticate — but don't abort if auth fails and we have fallback tokens
            var authResult = await loaded.Scraper.AuthenticateAsync(context, ct);
            if (!authResult.Authenticated)
            {
                _logger.LogWarning("[{Slug}] Primary authentication failed: {Msg}. Will try fallback tokens.", slug, authResult.Message);
                // Check if we have any fallback tokens (e.g., download_token from MiniDownloader)
                var fallbackToken = await context.TokenStore.GetTokenAsync("download_token", ct);
                if (string.IsNullOrEmpty(fallbackToken))
                {
                    status.Error = $"Auth failed: {authResult.Message}";
                    syncStatus = "failed";
                    syncError = status.Error;
                    _logger.LogError("[{Slug}] No fallback tokens available. Authenticate via the Plugins page.", slug);
                    return;
                }
                _logger.LogInformation("[{Slug}] Using fallback download token for sync", slug);
            }

            // Fetch manifest
            var manifest = await loaded.Scraper.FetchManifestAsync(context, null, ct);
            status.TotalModels = manifest.Count;
            _logger.LogInformation("[{Slug}] Manifest has {Count} models", slug, manifest.Count);

            // Update SyncRun with total count
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                var run = await db.SyncRuns.FindAsync([syncRunId], ct);
                if (run != null)
                {
                    run.TotalModels = manifest.Count;
                    await db.SaveChangesAsync(ct);
                }
            }

            // Load skip list from config
            var skipCreators = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (loaded.Scraper is ILibraryScraper scraper)
            {
                var ctx = await BuildPluginContextAsync(slug, scraper, ct);
                if (ctx.Config.TryGetValue("SKIP_CREATORS", out var skipList) && !string.IsNullOrEmpty(skipList))
                {
                    foreach (var s in skipList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        skipCreators.Add(s);
                }
            }

            // Scrape each model
            int processedSinceLastUpdate = 0;
            int manifestIndex = 0;
            foreach (var model in manifest)
            {
                if (ct.IsCancellationRequested) break;

                var currentIndex = manifestIndex++;

                // Skip entries before startIndex (resume support)
                if (currentIndex < startIndex)
                    continue;

                // Skip creators in the skip list
                if (!string.IsNullOrEmpty(model.CreatorName) && skipCreators.Contains(model.CreatorName))
                {
                    skipped++;
                    status.ScrapedModels = scraped;
                    processedSinceLastUpdate++;
                    continue;
                }

                var creatorDir = SanitizePath(model.CreatorName ?? "unknown");
                var modelName = SanitizePath(model.Name);

                // Fuzzy match: find existing directory that matches (handles name variations)
                var modelDir = FindExistingModelDir(context.SourceDirectory, creatorDir, modelName)
                    ?? Path.Combine(context.SourceDirectory, creatorDir, modelName);
                Directory.CreateDirectory(modelDir);
                context.ModelDirectory = modelDir;

                var result = await loaded.Scraper.ScrapeModelAsync(context, model, ct);
                if (result.Success)
                    scraped++;
                else
                {
                    failed++;
                    _logger.LogWarning("[{Slug}] Failed to scrape {Model}: {Error}",
                        slug, model.Name, result.Error);
                }

                status.ScrapedModels = scraped;
                status.FailedModels = failed;
                processedSinceLastUpdate++;

                // Persist progress every 10 models (also tracks LastProcessedIndex for resume)
                if (processedSinceLastUpdate >= 10)
                {
                    processedSinceLastUpdate = 0;
                    try
                    {
                        await using var db = await dbFactory.CreateDbContextAsync(ct);
                        var run = await db.SyncRuns.FindAsync([syncRunId], ct);
                        if (run != null)
                        {
                            run.ScrapedModels = scraped;
                            run.FailedModels = failed;
                            run.SkippedModels = skipped;
                            run.LastProcessedIndex = currentIndex + 1; // next index to process
                            await db.SaveChangesAsync(ct);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[{Slug}] Failed to update SyncRun progress", slug);
                    }
                }
            }

            _logger.LogInformation("[{Slug}] Sync complete: {Scraped} scraped, {Failed} failed, {Skipped} skipped",
                slug, scraped, failed, skipped);
        }
        catch (Exception ex)
        {
            status.Error = ex.Message;
            syncStatus = "failed";
            syncError = ex.Message;
            _logger.LogError(ex, "[{Slug}] Sync error", slug);
        }
        finally
        {
            status.IsRunning = false;
            status.CurrentProgress = null;
            status.SyncCts?.Dispose();
            status.SyncCts = null;
            semaphore.Release();

            // Finalize SyncRun record
            try
            {
                var completedAt = DateTime.UtcNow;
                await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
                var run = await db.SyncRuns.FindAsync(syncRunId);
                if (run != null)
                {
                    run.Status = syncStatus;
                    run.CompletedAt = completedAt;
                    run.DurationSeconds = (completedAt - startedAt).TotalSeconds;
                    run.ScrapedModels = scraped;
                    run.FailedModels = failed;
                    run.SkippedModels = skipped;
                    run.Error = syncError;
                    await db.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Slug}] Failed to finalize SyncRun record", slug);
            }
        }
    }

    // ─── Pre-load directory info (used for conflict detection before DLL loading) ──────────────

    private sealed record PluginDirInfo(
        string Dir,
        string? Slug,
        string? Version,
        long DllBytes,
        DateTime DllMtime,
        string? DllPath);

    private List<PluginDirInfo> GatherPluginDirInfos(string baseDir)
    {
        var results = new List<PluginDirInfo>();
        if (!Directory.Exists(baseDir)) return results;

        foreach (var pluginDir in Directory.GetDirectories(baseDir))
        {
            // Try to read manifest for slug/version without loading the DLL
            string? slug = null;
            string? version = null;
            try
            {
                var manifest = _manifestValidator.LoadManifest(pluginDir);
                slug = manifest?.Slug;
                version = manifest?.Version;
            }
            catch { /* manifest is optional */ }

            // Fall back to directory name as slug identifier
            slug ??= Path.GetFileName(pluginDir);

            // Find primary DLL (for size/mtime metadata in conflict warnings)
            string? dllPath = null;
            long dllBytes = 0;
            DateTime dllMtime = DateTime.MinValue;
            try
            {
                dllPath = Directory
                    .GetFiles(pluginDir, "*.dll")
                    .Where(f => !Path.GetFileName(f).StartsWith("Forgekeeper.PluginSdk",
                        StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => new FileInfo(f).Length)
                    .FirstOrDefault();

                if (dllPath != null)
                {
                    var fi = new FileInfo(dllPath);
                    dllBytes = fi.Length;
                    dllMtime = fi.LastWriteTimeUtc;
                }
            }
            catch { /* best-effort */ }

            results.Add(new PluginDirInfo(pluginDir, slug, version, dllBytes, dllMtime, dllPath));
        }

        return results;
    }

    private static string ComputeMd5(string filePath)
    {
        using var md5 = MD5.Create();
        using var stream = File.OpenRead(filePath);
        var hash = md5.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ─── Plugin discovery (dual-directory, conflict-aware) ────────────────────────────────────

    private async Task DiscoverPluginsAsync(CancellationToken ct)
    {
        // Emit startup precedence banner (R3)
        _logger.LogInformation(
            "Plugin source precedence: {Mode}",
            _preferBundled
                ? "app-over-data (image-bundled plugins win; /data/ override is ignored when bundled copy exists)"
                : "data-over-app (legacy; /data/ plugins override image-bundled — set Plugins:PreferBundled=true for immutable deployments)");

        // Gather lightweight info from both directories (no DLL loading yet)
        var builtinEntries = GatherPluginDirInfos(_builtinPluginsDirectory);
        var installedEntries = GatherPluginDirInfos(_pluginsDirectory);

        if (builtinEntries.Count == 0 && installedEntries.Count == 0)
        {
            _logger.LogWarning(
                "No plugin directories found in either {BuiltinDir} or {InstalledDir}",
                _builtinPluginsDirectory, _pluginsDirectory);
            return;
        }

        // Build slug → info maps for conflict detection
        var builtinBySlug = builtinEntries
            .GroupBy(e => e.Slug!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var installedBySlug = installedEntries
            .GroupBy(e => e.Slug!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // R2: Detect and warn about conflicts BEFORE any DLL is loaded
        bool abortDueToHashDrift = false;
        var conflictSlugs = new HashSet<string>(
            builtinBySlug.Keys.Intersect(installedBySlug.Keys, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        foreach (var slug in conflictSlugs)
        {
            var builtin = builtinBySlug[slug];
            var installed = installedBySlug[slug];
            var winner = _preferBundled ? builtin : installed;

            _logger.LogWarning("Plugin '{Slug}' found in multiple locations:", slug);
            _logger.LogWarning(
                "  {Dir} (v{Version}, {Bytes} bytes, mtime {Mtime:yyyy-MM-dd HH:mm} UTC) [image-bundled]",
                builtin.Dir,
                builtin.Version ?? "?",
                builtin.DllBytes,
                builtin.DllMtime);
            _logger.LogWarning(
                "  {Dir} (v{Version}, {Bytes} bytes, mtime {Mtime:yyyy-MM-dd HH:mm} UTC) [installed]",
                installed.Dir,
                installed.Version ?? "?",
                installed.DllBytes,
                installed.DllMtime);
            _logger.LogWarning(
                "Loading from {Dir} (current precedence: {Mode}). Set Plugins:PreferBundled={Toggle} to reverse.",
                winner.Dir,
                _preferBundled ? "app-over-data" : "data-over-app",
                !_preferBundled);

            // R4: Hash drift check — same version, different bytes → likely stale /data/ after rebuild
            if (_warnOnVersionDrift
                && builtin.Version != null
                && builtin.Version == installed.Version
                && builtin.DllPath != null
                && installed.DllPath != null)
            {
                try
                {
                    var builtinHash = ComputeMd5(builtin.DllPath);
                    var installedHash = ComputeMd5(installed.DllPath);

                    if (!string.Equals(builtinHash, installedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogError(
                            "Plugin '{Slug}' v{Version} hash drift detected!",
                            slug, builtin.Version);
                        _logger.LogError(
                            "  {Path} = {Hash}",
                            builtin.DllPath, builtinHash);
                        _logger.LogError(
                            "  {Path} = {Hash}",
                            installed.DllPath, installedHash);
                        _logger.LogError(
                            "Same version, different bytes — likely stale /data/ after image rebuild. Aborting.");
                        _logger.LogError(
                            "To resolve: delete {InstalledDir}, or set Plugins:WarnOnVersionDrift=false.",
                            installed.Dir);
                        abortDueToHashDrift = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Plugin '{Slug}': could not compute hash for drift check — skipping", slug);
                }
            }
        }

        if (abortDueToHashDrift)
            throw new InvalidOperationException(
                "Plugin hash drift detected. Remove stale /data/ plugin(s) or set Plugins:WarnOnVersionDrift=false to proceed.");

        // Load in precedence order:
        //   PreferBundled=false (default): builtin first, installed overwrites conflicts (data wins)
        //   PreferBundled=true:            installed first, builtin overwrites conflicts (app wins)
        var firstPassEntries  = _preferBundled ? installedEntries : builtinEntries;
        var secondPassEntries = _preferBundled ? builtinEntries   : installedEntries;

        foreach (var entry in firstPassEntries)
            await LoadPluginFromDirAsync(entry.Dir, ct);

        foreach (var entry in secondPassEntries)
            await LoadPluginFromDirAsync(entry.Dir, ct);
    }

    /// <summary>
    /// Load (or reload) a single plugin directory. Handles manifest validation, SDK compat check,
    /// DLL loading, and adds to the _plugins dictionary. Safe to call on already-loaded plugins
    /// (overwrites existing entry).
    /// </summary>
    private Task LoadPluginFromDirAsync(string pluginDir, CancellationToken ct)
    {
        try
        {
            // Load and validate manifest (optional — backward compat)
            var manifest = _manifestValidator.LoadManifest(pluginDir);
            ManifestValidationResult? validationResult = null;
            SdkCompatResult? compatResult = null;

            if (manifest is not null)
            {
                validationResult = _manifestValidator.Validate(manifest);

                foreach (var warn in validationResult.Warnings)
                    _logger.LogWarning("[manifest:{Dir}] {Warning}", Path.GetFileName(pluginDir), warn);

                foreach (var error in validationResult.Errors)
                    _logger.LogError("[manifest:{Dir}] {Error}", Path.GetFileName(pluginDir), error);

                // SDK compatibility check
                compatResult = _sdkChecker.CheckCompatibility(manifest);

                if (compatResult.Level == SdkCompatLevel.MajorMismatch)
                {
                    if (_forceLoadIncompatible)
                    {
                        _logger.LogWarning(
                            "[{Dir}] ⚠️ FORCE LOADING incompatible plugin (major SDK mismatch): {Reason}",
                            Path.GetFileName(pluginDir), compatResult.Reason);
                    }
                    else
                    {
                        _logger.LogError(
                            "[{Dir}] Refusing to load plugin — major SDK mismatch: {Reason}",
                            Path.GetFileName(pluginDir), compatResult.Reason);
                        return Task.CompletedTask;
                    }
                }
                else if (compatResult.Level == SdkCompatLevel.MinorMismatch)
                {
                    _logger.LogWarning(
                        "[{Dir}] Loading plugin with minor SDK mismatch (may be unstable): {Reason}",
                        Path.GetFileName(pluginDir), compatResult.Reason);
                }
            }
            else
            {
                _logger.LogWarning(
                    "[{Dir}] No manifest.json found — loading without validation (legacy plugin)",
                    Path.GetFileName(pluginDir));
            }

            var dlls = Directory.GetFiles(pluginDir, "*.dll")
                .Where(f => !Path.GetFileName(f).StartsWith("Forgekeeper.PluginSdk"))
                .ToArray();

            foreach (var dll in dlls)
            {
                var loadContext = new PluginLoadContext(dll);
                var assembly = loadContext.LoadFromAssemblyPath(dll);

                var scraperTypes = assembly.GetTypes()
                    .Where(t => typeof(ILibraryScraper).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

                foreach (var type in scraperTypes)
                {
                    var scraper = (ILibraryScraper)Activator.CreateInstance(type)!;

                    // If manifest has a slug, verify it matches the plugin's SourceSlug
                    if (manifest is not null && !string.IsNullOrWhiteSpace(manifest.Slug) &&
                        !string.Equals(manifest.Slug, scraper.SourceSlug, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogError(
                            "[{Dir}] Manifest slug '{ManifestSlug}' does not match plugin SourceSlug '{PluginSlug}'",
                            Path.GetFileName(pluginDir), manifest.Slug, scraper.SourceSlug);
                    }

                    _plugins[scraper.SourceSlug] = new LoadedPlugin
                    {
                        Scraper = scraper,
                        Assembly = assembly,
                        LoadContext = loadContext,
                        LoadedAt = DateTime.UtcNow,
                        Manifest = manifest,
                        ValidationResult = validationResult,
                        CompatResult = compatResult,
                        Source = DetermineSource(pluginDir),
                        SourceDirectory = pluginDir,
                    };
                    // R1: Always log the full DLL path so stale-file bugs are instantly visible
                    _logger.LogInformation(
                        "Loaded plugin: {Name} v{Version} ({Slug}) from {DllPath}",
                        scraper.SourceName, scraper.Version, scraper.SourceSlug, dll);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load plugin from {Dir}", pluginDir);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Hot-reload all plugins: unload all AssemblyLoadContexts, re-discover from original directories.
    /// Requires HotReloadEnabled = true in config.
    /// </summary>
    public async Task<object> ReloadAllAsync(CancellationToken ct)
    {
        _logger.LogInformation("Hot-reload: reloading all plugins");

        // Collect source directories before unloading
        var pluginDirs = _plugins.Values
            .Select(p => p.SourceDirectory)
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct()
            .ToList();

        // Unload all loaded plugins
        var slugs = _plugins.Keys.ToList();
        foreach (var slug in slugs)
        {
            if (_plugins.TryRemove(slug, out var plugin))
            {
                _logger.LogInformation("Hot-reload: unloading {Slug}", slug);
                plugin.LoadContext.Unload();
            }
        }

        // GC to release file locks
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Brief pause to let file handles release
        await Task.Delay(200, ct);

        // Re-load from collected directories + scan for new ones
        var dirsToLoad = new HashSet<string>(pluginDirs, StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(_builtinPluginsDirectory))
        {
            foreach (var dir in Directory.GetDirectories(_builtinPluginsDirectory))
                dirsToLoad.Add(dir);
        }
        if (Directory.Exists(_pluginsDirectory))
        {
            foreach (var dir in Directory.GetDirectories(_pluginsDirectory))
                dirsToLoad.Add(dir);
        }

        int loadErrors = 0;
        foreach (var dir in dirsToLoad)
        {
            try
            {
                await LoadPluginFromDirAsync(dir, ct);
            }
            catch (Exception ex)
            {
                loadErrors++;
                _logger.LogError(ex, "Hot-reload: error loading {Dir}", dir);
            }
        }

        _logger.LogInformation("Hot-reload complete: {Count} plugin(s) loaded", _plugins.Count);
        return new
        {
            loaded = _plugins.Count,
            plugins = _plugins.Keys.ToList(),
            errors = loadErrors,
        };
    }

    /// <summary>
    /// Hot-reload a single plugin by slug: unload its AssemblyLoadContext and re-load from same directory.
    /// Requires HotReloadEnabled = true in config.
    /// </summary>
    public async Task<object?> ReloadPluginAsync(string slug, CancellationToken ct)
    {
        if (!_plugins.TryGetValue(slug, out var existing))
            return null;

        var sourceDir = existing.SourceDirectory;
        if (string.IsNullOrEmpty(sourceDir))
            return null;

        _logger.LogInformation("Hot-reload: reloading plugin {Slug} from {Dir}", slug, sourceDir);

        // Unload
        _plugins.TryRemove(slug, out _);
        existing.LoadContext.Unload();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        await Task.Delay(100, ct);

        // Reload
        await LoadPluginFromDirAsync(sourceDir, ct);

        if (_plugins.TryGetValue(slug, out var reloaded))
        {
            _logger.LogInformation("Hot-reload: {Slug} reloaded successfully", slug);
            return new
            {
                slug,
                loaded = true,
                loadedAt = reloaded.LoadedAt,
                version = reloaded.Manifest?.Version ?? reloaded.Scraper.Version,
                sdkCompat = reloaded.CompatResult?.Level.ToString(),
                manifestValid = reloaded.ValidationResult?.IsValid,
                source = reloaded.Source,
            };
        }

        return new { slug, loaded = false, error = "Plugin not found after reload — check logs for errors" };
    }

    /// <summary>Determines the plugin source based on directory naming conventions.</summary>
    private static string DetermineSource(string pluginDir)
    {
        var dirName = Path.GetFileName(pluginDir) ?? "";
        // Convention: builtin plugins are in the image; others dropped manually
        // Future: registry/github sources will set this explicitly
        return "builtin";
    }

    /// <summary>Create a plugin context for external use (e.g., manifest upload).</summary>
    public async Task<PluginContext> CreateContextAsync(string slug, CancellationToken ct)
    {
        if (!_plugins.TryGetValue(slug, out var loaded))
            throw new InvalidOperationException($"Plugin '{slug}' not found");

        return await BuildPluginContextAsync(slug, loaded.Scraper, ct);
    }

    private async Task<PluginContext> BuildPluginContextAsync(string slug, ILibraryScraper scraper, CancellationToken ct)
    {
        var dbFactory = _services.GetRequiredService<IDbContextFactory<ForgeDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var configs = await db.PluginConfigs
            .Where(c => c.PluginSlug == slug && !c.Key.StartsWith("__token__"))
            .ToDictionaryAsync(c => c.Key, c => c.Value, ct);

        var sourceDir = Path.Combine(_sourcesDirectory, slug);
        Directory.CreateDirectory(sourceDir);

        var loggerFactory = _services.GetRequiredService<ILoggerFactory>();
        var tokenStore = new DbTokenStore(dbFactory, slug);

        return new PluginContext
        {
            SourceDirectory = sourceDir,
            Config = configs,
            HttpClient = new HttpClient(),
            Logger = loggerFactory.CreateLogger($"Plugin.{slug}"),
            TokenStore = tokenStore,
            Progress = new Progress<ScrapeProgress>(),
        };
    }

    private async Task<TimeSpan> GetSyncIntervalAsync(string slug, CancellationToken ct)
    {
        var dbFactory = _services.GetRequiredService<IDbContextFactory<ForgeDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var interval = await db.PluginConfigs
            .FirstOrDefaultAsync(c => c.PluginSlug == slug && c.Key == "SYNC_INTERVAL_HOURS", ct);

        if (interval is not null && double.TryParse(interval.Value, out var hours) && hours > 0)
            return TimeSpan.FromHours(hours);

        return TimeSpan.Zero; // Disabled by default
    }

    private static string SanitizePath(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
    }

    /// <summary>
    /// Fuzzy-match a model directory on disk. Handles name variations between
    /// manifest names and existing folder names (e.g., from MiniDownloader).
    /// Returns the existing path if found, null otherwise.
    /// </summary>
    private static string? FindExistingModelDir(string sourceDir, string creatorDir, string modelName)
    {
        // Exact match first
        var exactPath = Path.Combine(sourceDir, creatorDir, modelName);
        if (Directory.Exists(exactPath)) return exactPath;

        // Find creator directory (fuzzy)
        var creatorPath = FindFuzzyDir(sourceDir, creatorDir);
        if (creatorPath == null) return null;

        // Find model directory within creator (fuzzy)
        var modelPath = FindFuzzyDir(creatorPath, modelName);
        return modelPath;
    }

    /// <summary>
    /// Find a subdirectory by fuzzy matching: exact, case-insensitive,
    /// stripped punctuation/spaces, or prefix match.
    /// </summary>
    private static string? FindFuzzyDir(string parent, string targetName)
    {
        if (!Directory.Exists(parent)) return null;

        var normalized = NormalizeName(targetName);

        foreach (var dir in Directory.GetDirectories(parent))
        {
            var dirName = Path.GetFileName(dir);

            // Exact match
            if (dirName.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                return dir;

            // Normalized match (strip punctuation, collapse spaces)
            if (NormalizeName(dirName).Equals(normalized, StringComparison.OrdinalIgnoreCase))
                return dir;

            // Prefix match (handles "Creator - Model Name" vs "Model Name")
            var dashIdx = dirName.IndexOf(" - ");
            if (dashIdx > 0)
            {
                var afterDash = dirName[(dashIdx + 3)..];
                if (NormalizeName(afterDash).Equals(normalized, StringComparison.OrdinalIgnoreCase))
                    return dir;
            }
        }

        return null;
    }

    /// <summary>Normalize a name for comparison: lowercase, strip non-alphanumeric, collapse spaces.</summary>
    private static string NormalizeName(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}

/// <summary>Record of a loaded plugin.</summary>
public class LoadedPlugin
{
    public required ILibraryScraper Scraper { get; init; }
    public required Assembly Assembly { get; init; }
    public required AssemblyLoadContext LoadContext { get; init; }
    public DateTime LoadedAt { get; init; }
    public Forgekeeper.Core.Models.PluginManifest? Manifest { get; init; }
    public ManifestValidationResult? ValidationResult { get; init; }
    public SdkCompatResult? CompatResult { get; init; }
    /// <summary>Origin of the plugin: "builtin", "registry", "github", "manual".</summary>
    public string Source { get; init; } = "manual";
    /// <summary>Filesystem directory the plugin was loaded from (used for hot-reload).</summary>
    public string SourceDirectory { get; init; } = string.Empty;
}

/// <summary>Tracks sync status for a single plugin.</summary>
public class PluginSyncStatus
{
    public bool IsRunning { get; set; }
    public DateTime? LastSyncAt { get; set; }
    public int TotalModels { get; set; }
    public int ScrapedModels { get; set; }
    public int FailedModels { get; set; }
    public string? Error { get; set; }
    public ScrapeProgress? CurrentProgress { get; set; }
    
    /// <summary>CTS for cancelling the current sync. Set when sync starts, cleared when it ends.</summary>
    public CancellationTokenSource? SyncCts { get; set; }
}

/// <summary>Isolated assembly load context for plugin isolation.</summary>
internal class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginPath) : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Don't load shared assemblies from the plugin — use the host's version
        // This ensures ILibraryScraper and other SDK types are shared between host and plugin
        if (assemblyName.Name != null && (
            assemblyName.Name.StartsWith("Forgekeeper.") ||
            assemblyName.Name.StartsWith("Microsoft.Extensions.") ||
            assemblyName.Name.StartsWith("System.")))
        {
            return null; // Fall back to default (host) context
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path != null ? LoadFromAssemblyPath(path) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
