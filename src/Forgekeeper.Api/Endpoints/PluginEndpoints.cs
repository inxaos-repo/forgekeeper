using Forgekeeper.Core.Interfaces;
using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Data;
using Forgekeeper.Infrastructure.Services;
using Forgekeeper.PluginSdk;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SecretEncryption = Forgekeeper.Infrastructure.Services.SecretEncryption;


namespace Forgekeeper.Api.Endpoints;

public static class PluginEndpoints
{
    public static void MapPluginEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/plugins").WithTags("Plugins");

        // GET /api/v1/plugins — list installed plugins with status
        group.MapGet("/", (PluginHostService pluginHost) =>
        {
            var plugins = pluginHost.Plugins.Select(p => new PluginListResponse
            {
                Slug = p.Value.Scraper.SourceSlug,
                // Prefer manifest name/version if available, fall back to scraper interface values
                Name = p.Value.Manifest?.Name ?? p.Value.Scraper.SourceName,
                Description = p.Value.Manifest?.Description ?? p.Value.Scraper.Description,
                Version = p.Value.Manifest?.Version ?? p.Value.Scraper.Version,
                Author = p.Value.Manifest?.Author,
                ManifestValid = p.Value.ValidationResult?.IsValid,
                ManifestErrors = p.Value.ValidationResult?.Errors,
                ManifestWarnings = p.Value.ValidationResult?.Warnings,
                SdkCompatLevel = p.Value.CompatResult?.Level.ToString(),
                SdkCompatReason = p.Value.CompatResult?.Reason,
                Source = p.Value.Source,
                RequiresBrowserAuth = p.Value.Scraper.RequiresBrowserAuth,
                LoadedAt = p.Value.LoadedAt,
                SyncStatus = MapSyncStatus(pluginHost.GetSyncStatus(p.Key)),
            }).ToList();

            return Results.Ok(plugins);
        }).WithName("ListPlugins");

        // GET /api/v1/plugins/{slug}/config — get plugin config
        group.MapGet("/{slug}/config", async (
            string slug,
            PluginHostService pluginHost,
            ForgeDbContext db,
            CancellationToken ct) =>
        {
            var plugin = pluginHost.GetPlugin(slug);
            if (plugin is null) return Results.NotFound(new { message = $"Plugin '{slug}' not found" });

            var stored = await db.PluginConfigs
                .Where(c => c.PluginSlug == slug && !c.Key.StartsWith("__token__"))
                .ToDictionaryAsync(c => c.Key, c => c, ct);

            var fields = plugin.ConfigSchema.Select(f => new PluginConfigResponse
            {
                Key = f.Key,
                Label = f.Label,
                Type = f.Type.ToString().ToLowerInvariant(),
                Required = f.Required,
                HelpText = f.HelpText,
                DefaultValue = f.DefaultValue,
                // Mask secret values; decrypt encrypted entries for non-secret display
                Value = stored.TryGetValue(f.Key, out var entry)
                    ? (f.Type == PluginConfigFieldType.Secret
                        ? "••••••••"
                        : (entry.IsEncrypted ? TryDecrypt(entry.Value) : entry.Value))
                    : f.DefaultValue,
                IsSet = stored.ContainsKey(f.Key),
            }).ToList();

            return Results.Ok(fields);
        }).WithName("GetPluginConfig");

        // PUT /api/v1/plugins/{slug}/config — update plugin config
        group.MapPut("/{slug}/config", async (
            string slug,
            [FromBody] Dictionary<string, string> configValues,
            PluginHostService pluginHost,
            ForgeDbContext db,
            CancellationToken ct) =>
        {
            var plugin = pluginHost.GetPlugin(slug);
            if (plugin is null) return Results.NotFound(new { message = $"Plugin '{slug}' not found" });

            var validKeys = plugin.ConfigSchema.ToDictionary(f => f.Key, f => f);

            foreach (var (key, value) in configValues)
            {
                if (!validKeys.TryGetValue(key, out var field))
                    continue; // Skip unknown keys

                var entry = await db.PluginConfigs
                    .FirstOrDefaultAsync(c => c.PluginSlug == slug && c.Key == key, ct);

                if (entry is null)
                {
                    var isSecret = field.Type == PluginConfigFieldType.Secret;
                    entry = new PluginConfig
                    {
                        Id = Guid.NewGuid(),
                        PluginSlug = slug,
                        Key = key,
                        Value = isSecret ? SecretEncryption.Encrypt(value) : value,
                        IsEncrypted = isSecret,
                        UpdatedAt = DateTime.UtcNow,
                    };
                    db.PluginConfigs.Add(entry);
                }
                else
                {
                    // Don't overwrite secrets with the mask value
                    if (field.Type == PluginConfigFieldType.Secret && value == "••••••••")
                        continue;

                    var isSecret = field.Type == PluginConfigFieldType.Secret;
                    entry.Value = isSecret ? SecretEncryption.Encrypt(value) : value;
                    entry.IsEncrypted = isSecret;
                    entry.UpdatedAt = DateTime.UtcNow;
                }
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(new { message = "Config updated" });
        }).WithName("UpdatePluginConfig");

        // POST /api/v1/plugins/{slug}/sync — trigger sync for a plugin
        // ?resume=true to resume the last incomplete sync from its saved index
        group.MapPost("/{slug}/sync", async (
            string slug,
            [FromQuery] bool? resume,
            PluginHostService pluginHost,
            CancellationToken ct) =>
        {
            try
            {
                await pluginHost.TriggerSyncAsync(slug, resume == true, ct);
                var mode = resume == true ? "resumed" : "started";
                return Results.Accepted(value: new { message = $"Sync {mode} for '{slug}'" });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { message = ex.Message });
            }
        }).WithName("TriggerPluginSync");

        // GET /api/v1/plugins/{slug}/auth — initiate authentication flow
        // ?force=true to re-authenticate even if token exists
        group.MapGet("/{slug}/auth", async (
            string slug,
            [FromQuery] bool? force,
            PluginHostService pluginHost,
            CancellationToken ct) =>
        {
            var plugin = pluginHost.GetPlugin(slug);
            if (plugin is null) return Results.NotFound(new { message = $"Plugin '{slug}' not found" });

            var context = await pluginHost.CreateContextAsync(slug, ct);

            // If force=true, clear existing token so AuthenticateAsync generates a new auth URL
            if (force == true)
            {
                await context.TokenStore.DeleteTokenAsync("access_token", ct);
            }

            var result = await plugin.AuthenticateAsync(context, ct);

            // Always include the auth URL if available (for re-auth)
            if (result.Authenticated && force != true)
                return Results.Ok(new { authenticated = true, message = result.Message, authUrl = result.AuthUrl });

            if (!string.IsNullOrEmpty(result.AuthUrl))
                return Results.Ok(new { authenticated = false, authUrl = result.AuthUrl, message = result.Message });

            return Results.BadRequest(new { authenticated = false, message = result.Message });
        }).WithName("PluginAuth");

        // GET /api/v1/plugins/{slug}/progress — stream SSE sync progress
        group.MapGet("/{slug}/progress", async (
            string slug,
            HttpContext httpContext,
            PluginHostService pluginHost,
            CancellationToken ct) =>
        {
            var plugin = pluginHost.GetPlugin(slug);
            if (plugin is null)
            {
                httpContext.Response.StatusCode = 404;
                await httpContext.Response.WriteAsJsonAsync(new { message = $"Plugin '{slug}' not found" }, ct);
                return;
            }

            httpContext.Response.Headers["Content-Type"] = "text/event-stream";
            httpContext.Response.Headers["Cache-Control"] = "no-cache";
            httpContext.Response.Headers["X-Accel-Buffering"] = "no";
            httpContext.Response.Headers["Connection"] = "keep-alive";

            var writer = httpContext.Response.Body;

            async Task WriteSseAsync(string eventName, string jsonData)
            {
                var line = $"event: {eventName}\ndata: {jsonData}\n\n";
                var bytes = System.Text.Encoding.UTF8.GetBytes(line);
                await writer.WriteAsync(bytes, ct);
                await httpContext.Response.Body.FlushAsync(ct);
            }

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var status = pluginHost.GetSyncStatus(slug);

                    if (status is null)
                    {
                        await Task.Delay(2000, ct);
                        continue;
                    }

                    if (!status.IsRunning)
                    {
                        // Sync finished — emit complete event and close
                        var completePayload = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            scraped = status.ScrapedModels,
                            total = status.TotalModels,
                            failed = status.FailedModels
                        });
                        await WriteSseAsync("complete", completePayload);
                        break;
                    }

                    var progress = status.CurrentProgress;
                    var progressPayload = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        scraped = progress?.Current ?? status.ScrapedModels,
                        total = progress?.Total ?? status.TotalModels,
                        failed = status.FailedModels,
                        currentItem = progress?.CurrentItem,
                        status = progress?.Status ?? "running"
                    });
                    await WriteSseAsync("progress", progressPayload);

                    await Task.Delay(2000, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // Client disconnected — normal
            }
        }).WithName("GetPluginSyncProgress").Produces(200);

        // GET /api/v1/plugins/history — all sync run history
        group.MapGet("/history", async (
            [FromQuery] int? limit,
            ForgeDbContext db,
            CancellationToken ct) =>
        {
            var runs = await db.SyncRuns
                .OrderByDescending(r => r.StartedAt)
                .Take(limit ?? 50)
                .ToListAsync(ct);
            return Results.Ok(runs);
        }).WithName("GetAllSyncHistory");

        // GET /api/v1/plugins/{slug}/history — sync run history for a plugin
        group.MapGet("/{slug}/history", async (
            string slug,
            [FromQuery] int? limit,
            ForgeDbContext db,
            CancellationToken ct) =>
        {
            var runs = await db.SyncRuns
                .Where(r => r.PluginSlug == slug)
                .OrderByDescending(r => r.StartedAt)
                .Take(limit ?? 20)
                .ToListAsync(ct);
            return Results.Ok(runs);
        }).WithName("GetPluginSyncHistory");

        // POST /api/v1/plugins/install — install a plugin from a GitHub release URL
        group.MapPost("/install", async (
            [FromBody] PluginInstallRequest request,
            IPluginInstallService installService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Source))
                return Results.BadRequest(new { message = "'source' is required" });

            var result = await installService.InstallAsync(request.Source, request.Version, ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        }).WithName("InstallPlugin");

        // POST /api/v1/plugins/{slug}/update — update to latest version
        group.MapPost("/{slug}/update", async (
            string slug,
            IPluginInstallService installService,
            PluginHostService pluginHost,
            CancellationToken ct) =>
        {
            if (pluginHost.IsPluginSyncing(slug))
                return Results.Conflict(new { message = $"Plugin '{slug}' is currently syncing — update rejected. Stop the sync first." });

            var result = await installService.UpdateAsync(slug, ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        }).WithName("UpdatePlugin");

        // DELETE /api/v1/plugins/{slug} — uninstall a plugin
        group.MapDelete("/{slug}", async (
            string slug,
            IPluginInstallService installService,
            PluginHostService pluginHost,
            CancellationToken ct) =>
        {
            if (pluginHost.IsPluginSyncing(slug))
                return Results.Conflict(new { message = $"Plugin '{slug}' is currently syncing — remove rejected. Stop the sync first." });

            var success = await installService.RemoveAsync(slug, ct);
            if (!success)
                return Results.BadRequest(new { message = $"Failed to remove plugin '{slug}'. Check logs for details." });

            // Unload from runtime if hot-reload is enabled
            if (pluginHost.HotReloadEnabled)
                pluginHost.UnloadPlugin(slug);

            return Results.NoContent();
        }).WithName("RemovePlugin");

        // POST /api/v1/plugins/reload — hot-reload all plugins
        group.MapPost("/reload", async (PluginHostService pluginHost, CancellationToken ct) =>
        {
            if (!pluginHost.HotReloadEnabled)
                return Results.StatusCode(501); // Not Implemented
            var result = await pluginHost.ReloadAllAsync(ct);
            return Results.Ok(result);
        }).WithName("ReloadAllPlugins");

        // POST /api/v1/plugins/{slug}/reload — hot-reload single plugin
        group.MapPost("/{slug}/reload", async (string slug, PluginHostService pluginHost, CancellationToken ct) =>
        {
            if (!pluginHost.HotReloadEnabled)
                return Results.StatusCode(501); // Not Implemented
            var result = await pluginHost.ReloadPluginAsync(slug, ct);
            return result != null ? Results.Ok(result) : Results.NotFound(new { message = $"Plugin '{slug}' not found" });
        }).WithName("ReloadPlugin");

        // POST /api/v1/plugins/{slug}/sync/cancel — cancel a running sync
        group.MapPost("/{slug}/sync/cancel", (
            string slug,
            PluginHostService pluginHost) =>
        {
            var cancelled = pluginHost.CancelSync(slug);
            return cancelled
                ? Results.Ok(new { message = $"Sync cancellation requested for '{slug}'" })
                : Results.BadRequest(new { message = $"No running sync for '{slug}' to cancel" });
        }).WithName("CancelPluginSync");

        // GET /api/v1/plugins/registry — browse available plugins from the community registry
        group.MapGet("/registry", async (
            [FromQuery] string? search,
            [FromQuery] string? tag,
            [FromQuery] bool? forceRefresh,
            IPluginRegistryClient registry,
            CancellationToken ct) =>
        {
            var plugins = await registry.SearchAsync(search, tag, ct);
            return Results.Ok(plugins);
        }).WithName("BrowsePluginRegistry");

        // GET /api/v1/plugins/updates — available update summary
        group.MapGet("/updates", (PluginUpdateTracker tracker) =>
        {
            return Results.Ok(new
            {
                count = tracker.UpdateCount,
                updates = tracker.GetAvailableUpdates(),
            });
        }).WithName("GetAvailableUpdates");

        // GET /api/v1/plugins/{slug}/diagnostics — full plugin diagnostics
        group.MapGet("/{slug}/diagnostics", (string slug, PluginHostService pluginHost) =>
        {
            if (!pluginHost.Plugins.TryGetValue(slug, out var plugin))
                return Results.NotFound(new { message = $"Plugin '{slug}' not found" });

            return Results.Ok(new
            {
                slug,
                loaded = true,
                loadedAt = plugin.LoadedAt,
                source = plugin.Source,
                sourceDirectory = plugin.SourceDirectory,
                assemblyName = plugin.Assembly.GetName().ToString(),
                dllPath = plugin.Assembly.Location,
                manifest = plugin.Manifest,
                validation = plugin.ValidationResult is null ? null : new
                {
                    isValid = plugin.ValidationResult.IsValid,
                    errors = plugin.ValidationResult.Errors,
                    warnings = plugin.ValidationResult.Warnings,
                },
                sdkCompat = plugin.CompatResult is null ? null : new
                {
                    level = plugin.CompatResult.Level.ToString(),
                    isCompatible = plugin.CompatResult.IsCompatible,
                    reason = plugin.CompatResult.Reason,
                },
            });
        }).WithName("GetPluginDiagnostics");

        // GET /api/v1/plugins/{slug}/status — get sync status
        group.MapGet("/{slug}/status", (
            string slug,
            PluginHostService pluginHost) =>
        {
            var plugin = pluginHost.GetPlugin(slug);
            if (plugin is null) return Results.NotFound(new { message = $"Plugin '{slug}' not found" });

            var status = pluginHost.GetSyncStatus(slug);
            return Results.Ok(MapSyncStatus(status) ?? new PluginSyncStatusResponse());
        }).WithName("GetPluginSyncStatus");

        // POST /api/v1/plugins/{slug}/manifest — upload a manifest file
        group.MapPost("/{slug}/manifest", async (
            string slug,
            HttpRequest request,
            PluginHostService pluginHost,
            CancellationToken ct) =>
        {
            var plugin = pluginHost.GetPlugin(slug);
            if (plugin is null) return Results.NotFound(new { message = $"Plugin '{slug}' not found" });

            Stream? manifestStream = null;

            // Handle both multipart form upload and raw JSON body
            if (request.HasFormContentType)
            {
                var form = await request.ReadFormAsync(ct);
                var file = form.Files.GetFile("manifest");
                if (file is null)
                    return Results.BadRequest(new { message = "No manifest file uploaded" });
                manifestStream = file.OpenReadStream();
            }
            else
            {
                manifestStream = request.Body;
            }

            try
            {
                var context = await pluginHost.CreateContextAsync(slug, ct);
                var models = await plugin.FetchManifestAsync(context, manifestStream, ct);

                return Results.Ok(new
                {
                    message = $"Manifest loaded: {models.Count} models found",
                    modelCount = models.Count
                });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { message = $"Failed to parse manifest: {ex.Message}" });
            }
        }).WithName("UploadPluginManifest")
        .DisableAntiforgery();

        // Auth callback route — handles both direct query params and fragment extraction
        app.MapGet("/auth/{slug}/callback", async (
            string slug,
            HttpContext httpContext,
            PluginHostService pluginHost,
            CancellationToken ct) =>
        {
            var query = httpContext.Request.Query;

            // If we have auth params (from JS fragment redirect), process them
            if (query.ContainsKey("access_token") || query.ContainsKey("error"))
            {
                var callbackParams = query.ToDictionary(q => q.Key, q => q.Value.ToString());
                var result = await pluginHost.HandleAuthCallbackAsync(slug, callbackParams, ct);

                if (result.Authenticated)
                    return Results.Ok(new { message = result.Message, authenticated = true });
                else
                    return Results.BadRequest(new { message = result.Message, authenticated = false });
            }

            // No token params — serve the HTML page that extracts fragments
            return Results.Content("""
                <!DOCTYPE html>
                <html>
                <head><title>Connecting...</title>
                <style>body { font-family: system-ui; max-width: 400px; margin: 80px auto; text-align: center; }</style>
                </head>
                <body>
                <p>Connecting your account...</p>
                <script>
                    const hash = window.location.hash.substring(1);
                    if (!hash) {
                        document.body.innerHTML = '<h2>❌ No token received</h2><p>The authentication flow did not return a token.</p>';
                    } else {
                        const params = new URLSearchParams(hash);
                        fetch(window.location.pathname + '?' + params.toString())
                            .then(r => r.json())
                            .then(data => {
                                document.body.innerHTML = data.authenticated
                                    ? '<h2>✅ Connected!</h2><p>' + data.message + '</p><p>You can close this window and return to Forgekeeper.</p>'
                                    : '<h2>❌ Failed</h2><p>' + data.message + '</p>';
                            })
                            .catch(err => {
                                document.body.innerHTML = '<h2>❌ Error</h2><p>' + err.message + '</p>';
                            });
                    }
                </script>
                </body>
                </html>
            """, "text/html");
        }).WithTags("Plugins").WithName("PluginAuthCallback");
    }

    private static string TryDecrypt(string value)
    {
        try { return SecretEncryption.Decrypt(value); }
        catch { return value; } // Fallback for unencrypted legacy values
    }

    private static PluginSyncStatusResponse? MapSyncStatus(PluginSyncStatus? status)
    {
        if (status is null) return null;
        return new PluginSyncStatusResponse
        {
            IsRunning = status.IsRunning,
            LastSyncAt = status.LastSyncAt,
            TotalModels = status.TotalModels,
            ScrapedModels = status.ScrapedModels,
            FailedModels = status.FailedModels,
            Error = status.Error,
            CurrentProgress = status.CurrentProgress is not null ? new ScrapeProgressResponse
            {
                Status = status.CurrentProgress.Status,
                Current = status.CurrentProgress.Current,
                Total = status.CurrentProgress.Total,
                CurrentItem = status.CurrentProgress.CurrentItem,
            } : null,
        };
    }
}

// DTOs
public class PluginListResponse
{
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string? Author { get; set; }
    /// <summary>Whether the manifest.json passed validation. Null = no manifest.</summary>
    public bool? ManifestValid { get; set; }
    public List<string>? ManifestErrors { get; set; }
    public List<string>? ManifestWarnings { get; set; }
    /// <summary>SDK compatibility level: Compatible, MinorMismatch, MajorMismatch, Unknown. Null = no manifest.</summary>
    public string? SdkCompatLevel { get; set; }
    public string? SdkCompatReason { get; set; }
    /// <summary>Plugin origin: builtin, registry, github, manual.</summary>
    public string Source { get; set; } = "manual";
    public bool RequiresBrowserAuth { get; set; }
    public DateTime LoadedAt { get; set; }
    public PluginSyncStatusResponse? SyncStatus { get; set; }
}

public class PluginConfigResponse
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool Required { get; set; }
    public string? HelpText { get; set; }
    public string? DefaultValue { get; set; }
    public string? Value { get; set; }
    public bool IsSet { get; set; }
}

public class PluginSyncStatusResponse
{
    public bool IsRunning { get; set; }
    public DateTime? LastSyncAt { get; set; }
    public int TotalModels { get; set; }
    public int ScrapedModels { get; set; }
    public int FailedModels { get; set; }
    public string? Error { get; set; }
    public ScrapeProgressResponse? CurrentProgress { get; set; }
}

public class ScrapeProgressResponse
{
    public string Status { get; set; } = string.Empty;
    public int Current { get; set; }
    public int Total { get; set; }
    public string? CurrentItem { get; set; }
}

public class PluginInstallRequest
{
    /// <summary>GitHub URL or owner/repo shorthand. May include @version suffix.</summary>
    public string Source { get; set; } = "";

    /// <summary>Specific version/tag to install. null = latest.</summary>
    public string? Version { get; set; }
}
