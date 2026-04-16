using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Data;
using Forgekeeper.Infrastructure.Services;
using Forgekeeper.PluginSdk;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
                Name = p.Value.Scraper.SourceName,
                Description = p.Value.Scraper.Description,
                Version = p.Value.Scraper.Version,
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
                // Mask secret values
                Value = stored.TryGetValue(f.Key, out var entry)
                    ? (f.Type == PluginConfigFieldType.Secret ? "••••••••" : entry.Value)
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
                    entry = new PluginConfig
                    {
                        Id = Guid.NewGuid(),
                        PluginSlug = slug,
                        Key = key,
                        Value = value,
                        IsEncrypted = field.Type == PluginConfigFieldType.Secret,
                        UpdatedAt = DateTime.UtcNow,
                    };
                    db.PluginConfigs.Add(entry);
                }
                else
                {
                    // Don't overwrite secrets with the mask value
                    if (field.Type == PluginConfigFieldType.Secret && value == "••••••••")
                        continue;

                    entry.Value = value;
                    entry.UpdatedAt = DateTime.UtcNow;
                }
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(new { message = "Config updated" });
        }).WithName("UpdatePluginConfig");

        // POST /api/v1/plugins/{slug}/sync — trigger sync for a plugin
        group.MapPost("/{slug}/sync", async (
            string slug,
            PluginHostService pluginHost,
            CancellationToken ct) =>
        {
            try
            {
                await pluginHost.TriggerSyncAsync(slug, ct);
                return Results.Accepted(value: new { message = $"Sync started for '{slug}'" });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { message = ex.Message });
            }
        }).WithName("TriggerPluginSync");

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

        // Auth callback route
        app.MapGet("/auth/{slug}/callback", async (
            string slug,
            HttpContext httpContext,
            PluginHostService pluginHost,
            CancellationToken ct) =>
        {
            var callbackParams = httpContext.Request.Query
                .ToDictionary(q => q.Key, q => q.Value.ToString());

            var result = await pluginHost.HandleAuthCallbackAsync(slug, callbackParams, ct);

            if (result.Authenticated)
                return Results.Ok(new { message = result.Message, authenticated = true });
            else
                return Results.BadRequest(new { message = result.Message, authenticated = false });
        }).WithTags("Plugins").WithName("PluginAuthCallback");
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
