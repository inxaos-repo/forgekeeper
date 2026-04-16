using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
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
    private readonly ConcurrentDictionary<string, LoadedPlugin> _plugins = new();
    private readonly ConcurrentDictionary<string, PluginSyncStatus> _syncStatuses = new();
    private readonly string _pluginsDirectory;
    private readonly string _sourcesDirectory;

    public PluginHostService(
        IServiceProvider services,
        ILogger<PluginHostService> logger,
        IConfiguration configuration)
    {
        _services = services;
        _logger = logger;
        _pluginsDirectory = configuration["Forgekeeper:PluginsDirectory"] ?? "/app/plugins";
        _sourcesDirectory = configuration["Forgekeeper:SourcesDirectory"] ?? "/mnt/3dprinting/sources";
    }

    /// <summary>Get all loaded plugins.</summary>
    public IReadOnlyDictionary<string, LoadedPlugin> Plugins => _plugins;

    /// <summary>Get sync status for a plugin.</summary>
    public PluginSyncStatus? GetSyncStatus(string slug) =>
        _syncStatuses.TryGetValue(slug, out var status) ? status : null;

    /// <summary>Get a loaded plugin by slug.</summary>
    public ILibraryScraper? GetPlugin(string slug) =>
        _plugins.TryGetValue(slug, out var loaded) ? loaded.Scraper : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Plugin host starting, scanning {Dir}", _pluginsDirectory);

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

                var status = _syncStatuses.GetOrAdd(slug, _ => new PluginSyncStatus());
                if (status.IsRunning) continue;

                var interval = await GetSyncIntervalAsync(slug, stoppingToken);
                if (interval <= TimeSpan.Zero) continue;

                if (DateTime.UtcNow - status.LastSyncAt < interval) continue;

                _ = Task.Run(() => RunSyncAsync(slug, stoppingToken), stoppingToken);
            }
        }
    }

    /// <summary>Trigger a manual sync for a plugin.</summary>
    public async Task TriggerSyncAsync(string slug, CancellationToken ct)
    {
        if (!_plugins.ContainsKey(slug))
            throw new InvalidOperationException($"Plugin '{slug}' not found");

        var status = _syncStatuses.GetOrAdd(slug, _ => new PluginSyncStatus());
        if (status.IsRunning)
            throw new InvalidOperationException($"Sync for '{slug}' is already running");

        await Task.Run(() => RunSyncAsync(slug, ct), ct);
    }

    /// <summary>Handle an auth callback routed from the web server.</summary>
    public async Task<AuthResult> HandleAuthCallbackAsync(string slug, IDictionary<string, string> callbackParams, CancellationToken ct)
    {
        if (!_plugins.TryGetValue(slug, out var loaded))
            return AuthResult.Failed($"Plugin '{slug}' not found");

        var context = await BuildPluginContextAsync(slug, loaded.Scraper, ct);
        return await loaded.Scraper.HandleAuthCallbackAsync(context, callbackParams, ct);
    }

    private async Task RunSyncAsync(string slug, CancellationToken ct)
    {
        if (!_plugins.TryGetValue(slug, out var loaded)) return;

        var status = _syncStatuses.GetOrAdd(slug, _ => new PluginSyncStatus());
        status.IsRunning = true;
        status.LastSyncAt = DateTime.UtcNow;
        status.Error = null;

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

            // Authenticate
            var authResult = await loaded.Scraper.AuthenticateAsync(context, ct);
            if (!authResult.Authenticated)
            {
                status.Error = $"Auth failed: {authResult.Message}";
                _logger.LogWarning("[{Slug}] Authentication failed: {Msg}", slug, authResult.Message);
                return;
            }

            // Fetch manifest
            var manifest = await loaded.Scraper.FetchManifestAsync(context, null, ct);
            status.TotalModels = manifest.Count;
            _logger.LogInformation("[{Slug}] Manifest has {Count} models", slug, manifest.Count);

            // Scrape each model
            int scraped = 0, failed = 0;
            foreach (var model in manifest)
            {
                if (ct.IsCancellationRequested) break;

                var modelDir = Path.Combine(context.SourceDirectory,
                    SanitizePath(model.CreatorName ?? "unknown"),
                    SanitizePath(model.Name));
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
            }

            _logger.LogInformation("[{Slug}] Sync complete: {Scraped} scraped, {Failed} failed",
                slug, scraped, failed);
        }
        catch (Exception ex)
        {
            status.Error = ex.Message;
            _logger.LogError(ex, "[{Slug}] Sync error", slug);
        }
        finally
        {
            status.IsRunning = false;
            status.CurrentProgress = null;
        }
    }

    private async Task DiscoverPluginsAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_pluginsDirectory))
        {
            _logger.LogWarning("Plugins directory does not exist: {Dir}", _pluginsDirectory);
            return;
        }

        foreach (var pluginDir in Directory.GetDirectories(_pluginsDirectory))
        {
            try
            {
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
                        _plugins[scraper.SourceSlug] = new LoadedPlugin
                        {
                            Scraper = scraper,
                            Assembly = assembly,
                            LoadContext = loadContext,
                            LoadedAt = DateTime.UtcNow,
                        };
                        _logger.LogInformation("Loaded plugin: {Name} v{Version} ({Slug})",
                            scraper.SourceName, scraper.Version, scraper.SourceSlug);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load plugin from {Dir}", pluginDir);
            }
        }
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
}

/// <summary>Record of a loaded plugin.</summary>
public class LoadedPlugin
{
    public required ILibraryScraper Scraper { get; init; }
    public required Assembly Assembly { get; init; }
    public required AssemblyLoadContext LoadContext { get; init; }
    public DateTime LoadedAt { get; init; }
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
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path != null ? LoadFromAssemblyPath(path) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
