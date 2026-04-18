using Forgekeeper.Api.BackgroundServices;
using Forgekeeper.Infrastructure.Data;
using Forgekeeper.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Forgekeeper.Api.Endpoints;

public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1").WithTags("Settings");

        /// <summary>
        /// GET /api/v1/settings — Returns current read-only configuration.
        /// Settings come from appsettings.json / environment variables.
        /// To change a setting, set the corresponding environment variable
        /// (e.g. Scanner__IntervalHours=12) and restart the service.
        /// </summary>
        group.MapGet("/settings", async (
            IConfiguration config,
            ForgeDbContext db,
            PluginHostService pluginHost,
            HashWorker hashWorker,
            CancellationToken ct) =>
        {
            // Gather thumbnail progress from DB
            var totalModels = await db.Models.CountAsync(ct);
            var thumbnailsGenerated = await db.Models.CountAsync(m => m.ThumbnailPath != null, ct);

            // Calculate uptime
            var uptime = DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();

            return Results.Ok(new
            {
                // ─── Library ────────────────────────────────────────
                basePaths = config.GetSection("Storage:BasePaths").Get<string[]>() ?? new[] { "/mnt/3dprinting" },
                thumbnailDir = config["Storage:ThumbnailDir"] ?? ".forgekeeper/thumbnails",

                // ─── Scanner ────────────────────────────────────────
                scanIntervalHours = config.GetValue("Scanner:IntervalHours", 6),
                scanOnStartup = config.GetValue("Scanner:ScanOnStartup", true),
                scanFileTypes = config.GetSection("Scanner:FileTypes").Get<string[]>() ?? new[] { "stl", "obj", "3mf" },

                // ─── Import ─────────────────────────────────────────
                watchDirectories = config.GetSection("Import:WatchDirectories").Get<string[]>() ?? Array.Empty<string>(),
                autoImportEnabled = config.GetValue("Import:AutoImportEnabled", false),
                importIntervalMinutes = config.GetValue("Import:IntervalMinutes", 30),

                // ─── Thumbnails ──────────────────────────────────────
                thumbnailsEnabled = config.GetValue("Thumbnails:Enabled", true),
                thumbnailSize = config["Thumbnails:Size"] ?? "256x256",
                thumbnailFormat = config["Thumbnails:Format"] ?? "webp",
                thumbnailRenderer = config["Thumbnails:Renderer"] ?? "stl-thumb",
                thumbnailsBatchSize = config.GetValue("Thumbnails:BatchSize", 100),
                thumbnailsIntervalMinutes = config.GetValue("Thumbnails:IntervalMinutes", 5),
                thumbnailsGenerated,
                thumbnailsTotal = totalModels,

                // ─── Search ──────────────────────────────────────────
                minTrigramSimilarity = config.GetValue("Search:MinTrigramSimilarity", 0.3),
                resultsPerPage = config.GetValue("Search:ResultsPerPage", 50),

                // ─── Plugins ─────────────────────────────────────────
                pluginsDirectory = config["Forgekeeper:PluginsDirectory"] ?? "/app/plugins",
                sourcesDirectory = config["Forgekeeper:SourcesDirectory"] ?? "/mnt/3dprinting/sources",
                hotReloadEnabled = config.GetValue("Plugins:HotReloadEnabled", false),
                registryUrl = config["Plugins:RegistryUrl"] ?? "https://raw.githubusercontent.com/forgekeeper/plugin-registry/main/registry.json",
                registryCacheHours = config.GetValue("Plugins:RegistryCacheHours", 24),

                // ─── Plugin Auto-Update ───────────────────────────────
                autoUpdateEnabled = config.GetValue("Plugins:AutoUpdate:Enabled", false),
                autoUpdateMode = config["Plugins:AutoUpdate:Mode"] ?? "notify",
                autoUpdateIntervalHours = config.GetValue("Plugins:AutoUpdate:IntervalHours", 24),

                // ─── Hashing ──────────────────────────────────────────
                hashingEnabled = config.GetValue("Hashing:Enabled", true),
                hashingBatchSize = config.GetValue("Hashing:BatchSize", 50),
                hashingIntervalSeconds = config.GetValue("Hashing:IntervalSeconds", 5),
                hashingWorkerRunning = hashWorker.IsRunning,
                hashingTotalHashed = hashWorker.HashedCount,
                hashingErrors = hashWorker.ErrorCount,

                // ─── Security ─────────────────────────────────────────
                apiKeyConfigured = !string.IsNullOrEmpty(config["Security:ApiKey"]),

                // ─── System Info ──────────────────────────────────────
                version = "1.0.0",
                dotNetVersion = Environment.Version.ToString(),
                environment = config["ASPNETCORE_ENVIRONMENT"] ?? "Production",
                uptimeSeconds = (long)uptime.TotalSeconds,
                startedAt = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(),
                pluginsLoaded = pluginHost.Plugins.Count,
            });
        }).WithName("GetSettings");
    }
}
