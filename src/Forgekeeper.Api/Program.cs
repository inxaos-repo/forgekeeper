using Forgekeeper.Core.Enums;
using Forgekeeper.Core.Interfaces;
using Forgekeeper.Infrastructure.Data;
using Forgekeeper.Infrastructure.Repositories;
using Forgekeeper.Infrastructure.Services;
using Forgekeeper.Infrastructure.SourceAdapters;
using Forgekeeper.Api.BackgroundServices;
using Forgekeeper.Api.Endpoints;
using Forgekeeper.Api.Mcp;
using Forgekeeper.Api.Middleware;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Serilog
builder.Host.UseSerilog((context, config) =>
    config.ReadFrom.Configuration(context.Configuration)
        .WriteTo.Console());

// Database — configure NpgsqlDataSource with dynamic JSON support for JSONB columns
var connectionString = builder.Configuration.GetConnectionString("ForgeDb");
var npgsqlDataSource = new Npgsql.NpgsqlDataSourceBuilder(connectionString)
    .EnableDynamicJson()
    .Build();

builder.Services.AddDbContextFactory<ForgeDbContext>(options =>
    options.UseNpgsql(npgsqlDataSource)
        .UseSnakeCaseNamingConvention()
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

builder.Services.AddDbContext<ForgeDbContext>(options =>
    options.UseNpgsql(npgsqlDataSource)
        .UseSnakeCaseNamingConvention()
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

// Repositories
builder.Services.AddScoped<MetadataWritebackService>();
builder.Services.AddScoped<IModelRepository, ModelRepository>();
builder.Services.AddScoped<ICreatorRepository, CreatorRepository>();

// Source Adapters
builder.Services.AddSingleton<ISourceAdapter, MmfSourceAdapter>();
builder.Services.AddSingleton<ISourceAdapter>(
    new GenericSourceAdapter(SourceType.Thangs, "thangs"));
builder.Services.AddSingleton<ISourceAdapter>(
    new GenericSourceAdapter(SourceType.Cults3d, "cults3d"));
builder.Services.AddSingleton<ISourceAdapter>(
    new GenericSourceAdapter(SourceType.Thingiverse, "thingiverse"));
builder.Services.AddSingleton<ISourceAdapter>(
    new GenericSourceAdapter(SourceType.Manual, "manual"));
builder.Services.AddSingleton<ISourceAdapter, PatreonSourceAdapter>();

// Services
builder.Services.AddSingleton<IMetadataService, MetadataService>();
builder.Services.AddSingleton<IScannerService, FileScannerService>();
builder.Services.AddScoped<ISearchService, SearchService>();
builder.Services.AddScoped<IImportService, ImportService>();
builder.Services.AddSingleton<IThumbnailService, ThumbnailService>();
builder.Services.AddSingleton<NamingTemplateService>();

// Plugin system services
builder.Services.AddSingleton<ManifestValidationService>();
builder.Services.AddSingleton<SdkCompatibilityChecker>();

// Background Services
builder.Services.AddHostedService<ThumbnailWorker>();
builder.Services.AddHostedService<ScannerWorker>();
builder.Services.AddHostedService<ImportWorker>();
builder.Services.AddSingleton<PluginHostService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PluginHostService>());

// CORS for Vue.js dev server
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("http://localhost:5173", "http://localhost:3000")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

// Apply migrations on startup (skip for InMemory/testing)
if (!app.Environment.IsEnvironment("Testing"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
    await db.Database.MigrateAsync();
}

app.UseCors();
app.UseMiddleware<ApiKeyMiddleware>();
app.UseSerilogRequestLogging();

// Serve static files (Vue.js build output)
app.UseDefaultFiles();
app.UseStaticFiles();

// Health check
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

// Version info
app.MapGet("/version", () => Results.Ok(new
{
    Name = "Forgekeeper",
    Version = "1.0.0",
    BuildTime = File.Exists("/app/build-info.txt")
        ? File.ReadAllText("/app/build-info.txt").Trim()
        : "dev",
    DotNetVersion = Environment.Version.ToString(),
})).WithTags("System").WithName("GetVersion");

// Prometheus metrics endpoint
app.MapGet("/metrics", async (IServiceProvider services, PluginHostService pluginHost, CancellationToken ct) =>
{
    using var scope = services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

    // Model counts by source
    var modelsBySource = await db.Models
        .GroupBy(m => m.Source)
        .Select(g => new { Source = g.Key, Count = g.Count() })
        .ToListAsync(ct);

    long mmfCount = modelsBySource.FirstOrDefault(x => x.Source == Forgekeeper.Core.Enums.SourceType.Mmf)?.Count ?? 0;
    long manualCount = modelsBySource.FirstOrDefault(x => x.Source == Forgekeeper.Core.Enums.SourceType.Manual)?.Count ?? 0;

    var totalCreators = await db.Creators.CountAsync(ct);
    var totalFiles = await db.Variants.CountAsync(ct);
    var totalSizeBytes = await db.Models.SumAsync(m => m.TotalSizeBytes, ct);
    var thumbnailCount = await db.Models.CountAsync(m => m.ThumbnailPath != null, ct);
    var printedCount = await db.Models.CountAsync(m => m.PrintHistory != null && m.PrintHistory.Count > 0, ct);

    // Known plugin slugs (all loaded plugins)
    var pluginSlugs = pluginHost.Plugins.Keys.ToList();

    var sb = new System.Text.StringBuilder();

    // Models by source
    sb.AppendLine("# HELP forgekeeper_models_total Total models in library");
    sb.AppendLine("# TYPE forgekeeper_models_total gauge");
    sb.AppendLine($"forgekeeper_models_total{{source=\"mmf\"}} {mmfCount}");
    sb.AppendLine($"forgekeeper_models_total{{source=\"manual\"}} {manualCount}");
    sb.AppendLine();

    // Creators
    sb.AppendLine("# HELP forgekeeper_creators_total Total creators");
    sb.AppendLine("# TYPE forgekeeper_creators_total gauge");
    sb.AppendLine($"forgekeeper_creators_total {totalCreators}");
    sb.AppendLine();

    // Files
    sb.AppendLine("# HELP forgekeeper_files_total Total file variants");
    sb.AppendLine("# TYPE forgekeeper_files_total gauge");
    sb.AppendLine($"forgekeeper_files_total {totalFiles}");
    sb.AppendLine();

    // Library size
    sb.AppendLine("# HELP forgekeeper_library_size_bytes Total library size in bytes");
    sb.AppendLine("# TYPE forgekeeper_library_size_bytes gauge");
    sb.AppendLine($"forgekeeper_library_size_bytes {totalSizeBytes}");
    sb.AppendLine();

    // Thumbnails
    sb.AppendLine("# HELP forgekeeper_thumbnails_total Generated thumbnails count");
    sb.AppendLine("# TYPE forgekeeper_thumbnails_total gauge");
    sb.AppendLine($"forgekeeper_thumbnails_total {thumbnailCount}");
    sb.AppendLine();

    // Printed
    sb.AppendLine("# HELP forgekeeper_printed_total Models with print status printed");
    sb.AppendLine("# TYPE forgekeeper_printed_total gauge");
    sb.AppendLine($"forgekeeper_printed_total {printedCount}");
    sb.AppendLine();

    // Per-plugin sync metrics
    sb.AppendLine("# HELP forgekeeper_sync_running Is a sync currently running (1=yes, 0=no)");
    sb.AppendLine("# TYPE forgekeeper_sync_running gauge");
    foreach (var slug in pluginSlugs)
    {
        var s = pluginHost.GetSyncStatus(slug);
        sb.AppendLine($"forgekeeper_sync_running{{plugin=\"{slug}\"}} {(s?.IsRunning == true ? 1 : 0)}");
    }
    sb.AppendLine();

    sb.AppendLine("# HELP forgekeeper_sync_scraped_total Models scraped in current/last sync");
    sb.AppendLine("# TYPE forgekeeper_sync_scraped_total gauge");
    foreach (var slug in pluginSlugs)
    {
        var s = pluginHost.GetSyncStatus(slug);
        sb.AppendLine($"forgekeeper_sync_scraped_total{{plugin=\"{slug}\"}} {s?.ScrapedModels ?? 0}");
    }
    sb.AppendLine();

    sb.AppendLine("# HELP forgekeeper_sync_failed_total Models failed in current/last sync");
    sb.AppendLine("# TYPE forgekeeper_sync_failed_total gauge");
    foreach (var slug in pluginSlugs)
    {
        var s = pluginHost.GetSyncStatus(slug);
        sb.AppendLine($"forgekeeper_sync_failed_total{{plugin=\"{slug}\"}} {s?.FailedModels ?? 0}");
    }
    sb.AppendLine();

    sb.AppendLine("# HELP forgekeeper_sync_total_models Total models in current sync manifest");
    sb.AppendLine("# TYPE forgekeeper_sync_total_models gauge");
    foreach (var slug in pluginSlugs)
    {
        var s = pluginHost.GetSyncStatus(slug);
        sb.AppendLine($"forgekeeper_sync_total_models{{plugin=\"{slug}\"}} {s?.TotalModels ?? 0}");
    }
    sb.AppendLine();

    // Plugin health metrics
    sb.AppendLine("# HELP forgekeeper_plugins_loaded Whether a plugin is currently loaded (1=loaded)");
    sb.AppendLine("# TYPE forgekeeper_plugins_loaded gauge");
    foreach (var (slug, p) in pluginHost.Plugins)
        sb.AppendLine($"forgekeeper_plugins_loaded{{slug=\"{slug}\"}} 1");
    sb.AppendLine();

    sb.AppendLine("# HELP forgekeeper_plugins_manifest_valid Whether the plugin manifest is valid (1=valid, 0=invalid, -1=no manifest)");
    sb.AppendLine("# TYPE forgekeeper_plugins_manifest_valid gauge");
    foreach (var (slug, p) in pluginHost.Plugins)
    {
        var val = p.ValidationResult is null ? -1 : (p.ValidationResult.IsValid ? 1 : 0);
        sb.AppendLine($"forgekeeper_plugins_manifest_valid{{slug=\"{slug}\"}} {val}");
    }
    sb.AppendLine();

    sb.AppendLine("# HELP forgekeeper_plugins_sdk_compatible Whether the plugin SDK is compatible (1=compatible, 0=incompatible, -1=unknown)");
    sb.AppendLine("# TYPE forgekeeper_plugins_sdk_compatible gauge");
    foreach (var (slug, p) in pluginHost.Plugins)
    {
        int val = p.CompatResult?.Level switch
        {
            Forgekeeper.Infrastructure.Services.SdkCompatLevel.Compatible => 1,
            Forgekeeper.Infrastructure.Services.SdkCompatLevel.MinorMismatch => 1,
            Forgekeeper.Infrastructure.Services.SdkCompatLevel.MajorMismatch => 0,
            _ => -1,
        };
        sb.AppendLine($"forgekeeper_plugins_sdk_compatible{{slug=\"{slug}\"}} {val}");
    }

    return Results.Text(sb.ToString(), "text/plain; version=0.0.4; charset=utf-8");
}).WithTags("Metrics").WithName("GetMetrics");

// Map API endpoints
app.MapModelEndpoints();
app.MapCreatorEndpoints();
app.MapTagEndpoints();
app.MapScanEndpoints();
app.MapImportEndpoints();
app.MapStatsEndpoints();
app.MapVariantEndpoints();
app.MapSourceEndpoints();
app.MapPluginEndpoints();
app.MapFileEndpoints();

// Export endpoint — full library metadata dump for backup/restore
app.MapGet("/api/v1/export", async (
    ForgeDbContext db,
    HttpContext httpContext,
    CancellationToken ct) =>
{
    var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
    var filename = $"forgekeeper-export-{timestamp}.json";
    httpContext.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{filename}\"";

    var data = new
    {
        ExportedAt = DateTime.UtcNow,
        Version = "1.0",
        Creators = await db.Creators.ToListAsync(ct),
        Models = await db.Models
            .Include(m => m.Tags)
            .Include(m => m.Variants)
            .Include(m => m.RelationsFrom)
            .ToListAsync(ct),
        Tags = await db.Tags.ToListAsync(ct),
        Sources = await db.Sources.ToListAsync(ct),
        SyncRuns = await db.SyncRuns
            .OrderByDescending(r => r.StartedAt)
            .Take(100)
            .ToListAsync(ct),
        PluginConfigs = await db.PluginConfigs
            .Where(c => !c.IsEncrypted) // Never export secrets
            .ToListAsync(ct),
    };
    return Results.Ok(data);
}).WithTags("Export").WithName("ExportLibrary");

// Import/Restore endpoint — upsert library data from an export JSON
app.MapPost("/api/v1/import/restore", async (
    ForgeDbContext db,
    ImportRestoreRequest request,
    CancellationToken ct) =>
{
    int created = 0, updated = 0, skipped = 0;

    // 1. Upsert Creators (match by Name + Source)
    var creatorIdMap = new Dictionary<Guid, Guid>(); // export ID -> local ID
    if (request.Creators != null)
    {
        foreach (var c in request.Creators)
        {
            var existing = await db.Creators
                .FirstOrDefaultAsync(x => x.Name == c.Name && x.Source == c.Source, ct);
            if (existing is null)
            {
                var newCreator = new Forgekeeper.Core.Models.Creator
                {
                    Id = Guid.NewGuid(),
                    Name = c.Name,
                    Source = c.Source,
                    SourceUrl = c.SourceUrl,
                    ExternalId = c.ExternalId,
                    AvatarUrl = c.AvatarUrl,
                    ModelCount = c.ModelCount,
                    CreatedAt = c.CreatedAt,
                    UpdatedAt = c.UpdatedAt,
                };
                db.Creators.Add(newCreator);
                creatorIdMap[c.Id] = newCreator.Id;
                created++;
            }
            else
            {
                existing.SourceUrl = c.SourceUrl ?? existing.SourceUrl;
                existing.ExternalId = c.ExternalId ?? existing.ExternalId;
                existing.AvatarUrl = c.AvatarUrl ?? existing.AvatarUrl;
                existing.UpdatedAt = DateTime.UtcNow;
                creatorIdMap[c.Id] = existing.Id;
                updated++;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    // 2. Upsert Tags (match by Name)
    var tagIdMap = new Dictionary<Guid, Guid>();
    if (request.Tags != null)
    {
        foreach (var t in request.Tags)
        {
            var existing = await db.Tags.FirstOrDefaultAsync(x => x.Name == t.Name, ct);
            if (existing is null)
            {
                var newTag = new Forgekeeper.Core.Models.Tag
                {
                    Id = Guid.NewGuid(),
                    Name = t.Name,
                    Source = t.Source,
                };
                db.Tags.Add(newTag);
                tagIdMap[t.Id] = newTag.Id;
                created++;
            }
            else
            {
                tagIdMap[t.Id] = existing.Id;
                skipped++;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    // 3. Upsert Models (match by SourceId + Source, or BasePath)
    var modelIdMap = new Dictionary<Guid, Guid>();
    if (request.Models != null)
    {
        foreach (var m in request.Models)
        {
            // Resolve creator ID
            if (!creatorIdMap.TryGetValue(m.CreatorId, out var localCreatorId))
            {
                // Try to find by existing ID (if same DB)
                var creatorExists = await db.Creators.AnyAsync(c => c.Id == m.CreatorId, ct);
                localCreatorId = creatorExists ? m.CreatorId : Guid.Empty;
            }
            if (localCreatorId == Guid.Empty)
            {
                skipped++;
                continue; // Can't restore without a valid creator
            }

            Forgekeeper.Core.Models.Model3D? existing = null;
            if (!string.IsNullOrEmpty(m.SourceId))
                existing = await db.Models
                    .Include(x => x.Tags)
                    .FirstOrDefaultAsync(x => x.SourceId == m.SourceId && x.Source == m.Source, ct);

            if (existing is null)
                existing = await db.Models
                    .Include(x => x.Tags)
                    .FirstOrDefaultAsync(x => x.BasePath == m.BasePath, ct);

            if (existing is null)
            {
                var newModel = new Forgekeeper.Core.Models.Model3D
                {
                    Id = Guid.NewGuid(),
                    CreatorId = localCreatorId,
                    Name = m.Name,
                    SourceId = m.SourceId,
                    Source = m.Source,
                    SourceUrl = m.SourceUrl,
                    Description = m.Description,
                    Category = m.Category,
                    Scale = m.Scale,
                    GameSystem = m.GameSystem,
                    FileCount = m.FileCount,
                    TotalSizeBytes = m.TotalSizeBytes,
                    ThumbnailPath = m.ThumbnailPath,
                    PreviewImages = m.PreviewImages ?? [],
                    BasePath = m.BasePath,
                    Rating = m.Rating,
                    Notes = m.Notes,
                    Extra = m.Extra,
                    LicenseType = m.LicenseType,
                    CollectionName = m.CollectionName,
                    PublishedAt = m.PublishedAt,
                    AcquisitionMethod = m.AcquisitionMethod,
                    PrintStatus = m.PrintStatus,
                    AcquisitionOrderId = m.AcquisitionOrderId,
                    ExternalCreatedAt = m.ExternalCreatedAt,
                    ExternalUpdatedAt = m.ExternalUpdatedAt,
                    DownloadedAt = m.DownloadedAt,
                    CreatedAt = m.CreatedAt,
                    UpdatedAt = m.UpdatedAt,
                };

                // Resolve and attach tags
                if (m.Tags != null)
                {
                    foreach (var t in m.Tags)
                    {
                        Forgekeeper.Core.Models.Tag? localTag = null;
                        if (tagIdMap.TryGetValue(t.Id, out var localTagId))
                            localTag = await db.Tags.FindAsync([localTagId], ct);
                        localTag ??= await db.Tags.FirstOrDefaultAsync(x => x.Name == t.Name, ct);
                        if (localTag != null)
                            newModel.Tags.Add(localTag);
                    }
                }

                db.Models.Add(newModel);
                modelIdMap[m.Id] = newModel.Id;
                created++;
            }
            else
            {
                // Update mutable fields
                existing.Name = m.Name;
                existing.Description = m.Description ?? existing.Description;
                existing.Category = m.Category ?? existing.Category;
                existing.GameSystem = m.GameSystem ?? existing.GameSystem;
                existing.ThumbnailPath = m.ThumbnailPath ?? existing.ThumbnailPath;
                existing.Rating = m.Rating ?? existing.Rating;
                existing.Notes = m.Notes ?? existing.Notes;
                existing.Extra = m.Extra ?? existing.Extra;
                existing.UpdatedAt = DateTime.UtcNow;
                modelIdMap[m.Id] = existing.Id;
                updated++;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    // 4. Upsert Variants (match by ModelId + FilePath)
    if (request.Models != null)
    {
        foreach (var m in request.Models)
        {
            if (m.Variants == null || m.Variants.Count == 0) continue;
            if (!modelIdMap.TryGetValue(m.Id, out var localModelId)) continue;

            foreach (var v in m.Variants)
            {
                var existing = await db.Variants
                    .FirstOrDefaultAsync(x => x.ModelId == localModelId && x.FilePath == v.FilePath, ct);

                if (existing is null)
                {
                    db.Variants.Add(new Forgekeeper.Core.Models.Variant
                    {
                        Id = Guid.NewGuid(),
                        ModelId = localModelId,
                        VariantType = v.VariantType,
                        FilePath = v.FilePath,
                        FileName = v.FileName,
                        FileType = v.FileType,
                        FileSizeBytes = v.FileSizeBytes,
                        ThumbnailPath = v.ThumbnailPath,
                        FileHash = v.FileHash,
                        CreatedAt = v.CreatedAt,
                    });
                    created++;
                }
                else
                {
                    skipped++;
                }
            }
        }
        await db.SaveChangesAsync(ct);
    }

    return Results.Ok(new { created, updated, skipped });
}).WithTags("Export").WithName("ImportRestore");

// --- MCP Endpoints ---
app.MapGet("/mcp/tools", () =>
    Results.Ok(ForgekeeperMcpServer.GetToolDefinitions()))
    .WithTags("MCP").WithName("McpListTools");

app.MapPost("/mcp/invoke", async (
    McpInvokeRequest request,
    IServiceProvider services,
    CancellationToken ct) =>
{
    var response = await ForgekeeperMcpServer.InvokeAsync(request, services, ct);
    return response.IsError ? Results.BadRequest(response) : Results.Ok(response);
}).WithTags("MCP").WithName("McpInvokeTool");

// SPA fallback — serve index.html for non-API, non-file routes
app.MapFallbackToFile("index.html");

app.Run();

// Needed for WebApplicationFactory<Program> in integration tests
public partial class Program { }

/// <summary>Request body for POST /api/v1/import/restore matching the export format.</summary>
public class ImportRestoreRequest
{
    public List<Forgekeeper.Core.Models.Creator>? Creators { get; set; }
    public List<Forgekeeper.Core.Models.Model3D>? Models { get; set; }
    public List<Forgekeeper.Core.Models.Tag>? Tags { get; set; }
    public List<Forgekeeper.Core.Models.Source>? Sources { get; set; }
}
